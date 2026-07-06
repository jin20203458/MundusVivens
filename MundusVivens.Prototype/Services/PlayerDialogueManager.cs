using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Helpers;
using MundusVivens.Prototype.Protos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IPlayerDialogueManager
    {
        Task<(bool Success, string Message, ulong SessionId, string Greeting)> StartDialogueAsync(string playerId, string npcId, CancellationToken cancellationToken = default);
        Task<string> SendMessageAsync(ulong sessionId, string messageText, CancellationToken cancellationToken = default);
        Task<(bool Success, string Summary)> EndDialogueAsync(ulong sessionId, CancellationToken cancellationToken = default);
        Task CleanupIdleSessionsAsync(TimeSpan idleTimeout, CancellationToken cancellationToken = default);
    }

    public class PlayerDialogueManager : IPlayerDialogueManager
    {
        private readonly IGeminiApiService _apiService;
        private readonly IBeliefEngine _beliefEngine;
        private readonly IWorldEventBroadcaster _broadcaster;
        private readonly MemoryEventLogger _memoryLogger;
        private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
        private readonly IEmbeddingCache _embeddingCache;

        private readonly IPersistenceService _persistence;

        private readonly ConcurrentDictionary<ulong, PlayerDialogueSession> _activeSessions = new();

        public PlayerDialogueManager(
            IGeminiApiService apiService,
            IBeliefEngine beliefEngine,
            IWorldEventBroadcaster broadcaster,
            MemoryEventLogger memoryLogger,
            Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
            IEmbeddingCache embeddingCache,
            IPersistenceService persistence)
        {
            _apiService = apiService;
            _beliefEngine = beliefEngine;
            _broadcaster = broadcaster;
            _memoryLogger = memoryLogger;
            _agentsAccessor = agentsAccessor;
            _embeddingCache = embeddingCache;
            _persistence = persistence;
        }

        public async Task<(bool Success, string Message, ulong SessionId, string Greeting)> StartDialogueAsync(string playerId, string npcId, CancellationToken cancellationToken = default)
        {
            var agents = _agentsAccessor();
            if (!agents.TryGetValue(playerId, out var player) || !agents.TryGetValue(npcId, out var npc))
            {
                return (false, "에이전트를 찾을 수 없습니다.", 0, string.Empty);
            }

            if (npc.Status.IsInConversation)
            {
                return (false, $"{npc.Persona.Name}은(는) 현재 다른 대화 중이어서 대화할 수 없습니다.", 0, string.Empty);
            }

            if (player.Status.IsInConversation)
            {
                return (false, "플레이어가 이미 대화 진행 중입니다.", 0, string.Empty);
            }

            // 🆕 플레이어 대화 시작 전 연상 기억 복원 (장소 및 플레이어 기준)
            var recalled = _persistence.RecallBeliefs(npc.AgentId, npc.Status.CurrentLocation, playerId, null, limit: 5);
            foreach (var belief in recalled)
            {
                npc.MemoryBox.AddOrUpdateBelief(belief);
            }

            // Lock the agents
            player.Status.IsInConversation = true;
            npc.Status.IsInConversation = true;
            player.Status.Activity = $"{npc.Persona.Name}와(과) 대화 중";
            npc.Status.Activity = $"플레이어와 대화 중";

            var session = new PlayerDialogueSession(playerId, npcId);
            _activeSessions[session.SessionId] = session;

            // 1. 대화 시작 이벤트 브로드캐스트
            var startEvent = new WorldEvent
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Dialogue = new DialogueEvent
                {
                    TaskId = session.SessionId,
                    AgentAId = player.NumericId,
                    AgentBId = npc.NumericId,
                    Location = LocationCoordinateRegistry.CreateLocationInfo(npc.Status.CurrentLocation),
                    IsStarted = true
                }
            };
            await _broadcaster.BroadcastAsync(startEvent);

            // 2. Greeting 생성
            var rel = GetOrCreateRelationship(npc, playerId);
            // 관련 기억 및 믿음 목록 선출
            var sortedBeliefs = ComputeTopKBeliefs(npc, player);

            var beliefLines = new List<string>();
            foreach (var b in sortedBeliefs)
            {
                double daysPassed = (DateTime.UtcNow - b.AcquiredAt).TotalDays;
                string timeTag = daysPassed switch
                {
                    < 0.04 => "[방금 전]",
                    < 1.0 => "[오늘]",
                    < 2.0 => "[어제]",
                    < 7.0 => $"[{(int)daysPassed}일 전]",
                    < 30.0 => $"[{(int)(daysPassed / 7.0)}주 전]",
                    < 365.0 => $"[{(int)(daysPassed / 30.0)}달 전]",
                    _ => $"[{(int)(daysPassed / 365.0)}년 전]"
                };
                string beliefTypeDesc = b.Type switch
                {
                    BeliefType.Core => "신념",
                    BeliefType.Witnessed => "직접 본 사건",
                    BeliefType.Heard => "전해 들은 이야기",
                    BeliefType.Overheard => "얼핏 엿들은 이야기",
                    _ => "기억"
                };
                beliefLines.Add($"- {timeTag} {beliefTypeDesc} (확신도: {PromptFormattingHelpers.GetConfidenceLabel(b.Confidence)}): \"{b.Content}\"");
            }

            string relevantMemoriesStr = string.Join("\n", beliefLines);
            if (string.IsNullOrWhiteSpace(relevantMemoriesStr))
            {
                relevantMemoriesStr = "플레이어에 대한 특별한 과거 기억이 없습니다.";
            }

            // 소문 공유 확인 (기억 안에서 선택)
            var beliefToShare = _beliefEngine.SelectBeliefToShare(npc, player, sortedBeliefs);
            session.NpcBeliefIdToShare = beliefToShare?.BeliefId;

            string gossipSnippet = "없음 (평범하게 첫 인사를 건네십시오)";
            if (beliefToShare != null)
            {
                gossipSnippet = $"[비밀 소문 폭로 지시] 대상: {beliefToShare.SubjectId}, 소문 내용: \"{beliefToShare.Content}\"\n" +
                    "지시: 첫 인사 대화 중 자연스럽게 플레이어에게 이 소문을 흘리거나 질문해 보십시오. (예: '마침 잘 왔군, 혹시 카일 소식 들었나?') 소문의 대상 인물 실명을 언급하십시오.";
            }

            string knownGossipsStr = relevantMemoriesStr;

            Console.WriteLine($"📋 [소문/믿음 압축] {npc.Persona.Name}: {npc.MemoryBox.Beliefs.Count} -> {sortedBeliefs.Count}개 선별 (플레이어 대화 시작)");

            string systemPrompt = $$"""
<role>가상 세계 시뮬레이션 NPC [{{npc.Persona.Name}}]</role>
<task>주어진 페르소나와 대화 상대에 대한 기억을 바탕으로 첫 대면 인사를 건네십시오.</task>

<context>
[내 페르소나]
- 이름/직업: {{npc.Persona.Name}} / {{npc.Persona.Job}}
- 성격/말투: {{npc.Persona.ToneStyle}}
- 배경 이야기: {{npc.Persona.Backstory}}
- 핵심 가치관: {{npc.Persona.CoreValues}}

[대화 상대방 정보]
- 이름/직업: {{player.Persona.Name}} / {{player.Persona.Job}}
- 상대에 대한 나의 태도: 호감도 {{rel.Liking}}/100 ({{PromptFormattingHelpers.GetLikingLabel(rel.Liking)}}), 신뢰도 {{rel.Trust}}/100 ({{PromptFormattingHelpers.GetTrustLabel(rel.Trust)}}){{(!string.IsNullOrEmpty(rel.ImpressionSummary) ? $", 인상/평가: \"{rel.ImpressionSummary}\"" : "")}}

[내가 알고 있는 소문 목록 (최대 3개 선별)]
{{knownGossipsStr}}

[기억 및 상황 맥락]
<relevant_memories>
{{relevantMemoriesStr}}
</relevant_memories>

<current_situation>
- 현재 위치: {{npc.Status.CurrentLocation}}
- 나의 감정 상태: {{npc.Status.Emotion}}
- 화두가 될 수 있는 소문: {{gossipSnippet}}
</current_situation>
</context>

<rules>
1. 대화 시나리오처럼 오직 캐릭터의 대사(" ")와 짧은 행동 묘사만 출력하십시오. (해설, 인사말 포맷 등 불필요한 텍스트 배제)
2. 주어진 캐릭터의 말투와 감정 상태를 철저히 반영하여 연기하십시오.
3. 첫 인사 한 줄만 출력하십시오. (최대 2문장 이내)
</rules>
""";

            var request = new GeminiRequest(
                SystemInstruction: new Content("system", new List<Part> { new Part(systemPrompt) }),
                Contents: new List<Content> { new Content("user", new List<Part> { new Part("인사를 시작하십시오.") }) },
                GenerationConfig: new GenerationConfig(null, 4000, "text/plain", null, new ThinkingConfig(ThinkingLevel.minimal))
            );

            string greeting = await _apiService.SendMessageAsync(request, ModelTier.Flash35, cancellationToken);
            greeting = CleanResponse(greeting);

            session.ConversationHistory.Add(new ChatMessage(npcId, greeting));

            return (true, string.Empty, session.SessionId, greeting);
        }

        public async Task<string> SendMessageAsync(ulong sessionId, string messageText, CancellationToken cancellationToken = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                throw new Exception("대화 세션을 찾을 수 없습니다.");
            }

            session.LastActiveAt = DateTime.UtcNow;

            var agents = _agentsAccessor();
            var player = agents[session.PlayerId];
            var npc = agents[session.NpcId];

            // 플레이어 메시지 기록
            session.ConversationHistory.Add(new ChatMessage(session.PlayerId, messageText));

            // NPC 응답 생성
            var rel = GetOrCreateRelationship(npc, session.PlayerId);
            // 관련 기억 및 믿음 목록 선출
            var sortedBeliefs = ComputeTopKBeliefs(npc, player);

            var beliefLines = new List<string>();
            foreach (var b in sortedBeliefs)
            {
                double daysPassed = (DateTime.UtcNow - b.AcquiredAt).TotalDays;
                string timeTag = daysPassed switch
                {
                    < 0.04 => "[방금 전]",
                    < 1.0 => "[오늘]",
                    < 2.0 => "[어제]",
                    < 7.0 => $"[{(int)daysPassed}일 전]",
                    < 30.0 => $"[{(int)(daysPassed / 7.0)}주 전]",
                    < 365.0 => $"[{(int)(daysPassed / 30.0)}달 전]",
                    _ => $"[{(int)(daysPassed / 365.0)}년 전]"
                };
                string beliefTypeDesc = b.Type switch
                {
                    BeliefType.Core => "신념",
                    BeliefType.Witnessed => "직접 본 사건",
                    BeliefType.Heard => "전해 들은 이야기",
                    BeliefType.Overheard => "얼핏 엿들은 이야기",
                    _ => "기억"
                };
                beliefLines.Add($"- {timeTag} {beliefTypeDesc} (확신도: {PromptFormattingHelpers.GetConfidenceLabel(b.Confidence)}): \"{b.Content}\"");
            }

            string relevantMemoriesStr = string.Join("\n", beliefLines);
            if (string.IsNullOrWhiteSpace(relevantMemoriesStr))
            {
                relevantMemoriesStr = "플레이어에 대한 특별한 과거 기억이 없습니다.";
            }

            // 소문 공유 확인 (세션 시작 시 선택된 소문 유지)
            Belief? beliefToShare = null;
            if (!string.IsNullOrWhiteSpace(session.NpcBeliefIdToShare))
            {
                npc.MemoryBox.Beliefs.TryGetValue(session.NpcBeliefIdToShare, out beliefToShare);
            }

            string gossipSnippet = "없음 (플레이어의 질문에 성실히 대답하십시오)";
            if (beliefToShare != null)
            {
                gossipSnippet = $"[비밀 소문 폭로 지시] 대상: {beliefToShare.SubjectId}, 소문 내용: \"{beliefToShare.Content}\"\n" +
                    "지시: 기회가 된다면 대화 흐름 중 자연스럽게 플레이어에게 이 소문을 흘리십시오. 소문의 대상 인물 실명을 언급하십시오.";
            }

            string knownGossipsStr = relevantMemoriesStr;

            Console.WriteLine($"📋 [소문/믿음 압축] {npc.Persona.Name}: {npc.MemoryBox.Beliefs.Count} -> {sortedBeliefs.Count}개 선별 (플레이어 메시지 전송)");

            string summarySection = session.ConversationSummary != string.Empty ? $"\n[이전 대화 요약]\n{session.ConversationSummary}\n" : string.Empty;
            string systemPrompt = $$"""
<role>가상 세계 시뮬레이션 NPC [{{npc.Persona.Name}}]</role>
<task>주어진 페르소나, 대화 상대에 대한 기억, 그리고 이전 대화 내역을 바탕으로 상대의 말에 적절한 답변을 완성하십시오.</task>

<context>
{{summarySection}}
[내 페르소나]
- 이름/직업: {{npc.Persona.Name}} / {{npc.Persona.Job}}
- 성격/말투: {{npc.Persona.ToneStyle}}
- 배경 이야기: {{npc.Persona.Backstory}}
- 핵심 가치관: {{npc.Persona.CoreValues}}

[대화 상대방 정보]
- 이름/직업: {{player.Persona.Name}} / {{player.Persona.Job}}
- 상대에 대한 나의 태도: 호감도 {{rel.Liking}}/100 ({{PromptFormattingHelpers.GetLikingLabel(rel.Liking)}}), 신뢰도 {{rel.Trust}}/100 ({{PromptFormattingHelpers.GetTrustLabel(rel.Trust)}}){{(!string.IsNullOrEmpty(rel.ImpressionSummary) ? $", 인상/평가: \"{rel.ImpressionSummary}\"" : "")}}

[내가 알고 있는 소문 목록 (최대 3개 선별)]
{{knownGossipsStr}}

[기억 및 상황 맥락]
<relevant_memories>
{{relevantMemoriesStr}}
</relevant_memories>

<current_situation>
- 현재 위치: {{npc.Status.CurrentLocation}}
- 나의 감정 상태: {{npc.Status.Emotion}}
- 화두가 될 수 있는 소문: {{gossipSnippet}}
</current_situation>
</context>

<rules>
1. 오직 캐릭터의 대사(" ")와 짧은 행동 묘사만 출력하십시오. (해설, 상황 설명 등 텍스트 배제)
2. 주어진 캐릭터의 말투와 감정 상태를 철저히 반영하여 연기하십시오.
3. 한 번의 호출에 단 한 줄의 대사(최대 2문장 이내)만 출력하십시오.
4. 오직 내 캐릭터의 반응만 작성하고, 다음 행동이나 대사는 플레이어의 턴에 맡기십시오.
</rules>
""";

            // 대화 이력 빌드
            var contents = new List<Content>();
            foreach (var msg in session.ConversationHistory)
            {
                string role = msg.Role == npc.AgentId ? "model" : "user";
                contents.Add(new Content(role, new List<Part> { new Part(msg.Text) }));
            }

            var request = new GeminiRequest(
                SystemInstruction: new Content("system", new List<Part> { new Part(systemPrompt) }),
                Contents: contents,
                GenerationConfig: new GenerationConfig(null, 4000, "text/plain", null, new ThinkingConfig(ThinkingLevel.minimal))
            );

            string reply = await _apiService.SendMessageAsync(request, ModelTier.Flash35, cancellationToken);
            reply = CleanResponse(reply);

            session.ConversationHistory.Add(new ChatMessage(npc.AgentId, reply));

            return reply;
        }

        public async Task<(bool Success, string Summary)> EndDialogueAsync(ulong sessionId, CancellationToken cancellationToken = default)
        {
            if (!_activeSessions.TryRemove(sessionId, out var session))
            {
                return (false, "대화 세션을 찾을 수 없습니다.");
            }

            var agents = _agentsAccessor();
            var player = agents[session.PlayerId];
            var npc = agents[session.NpcId];

            string rawHistory = string.Join("\n", session.ConversationHistory.Select(m => {
                string name = m.Role == player.AgentId ? player.Persona.Name : npc.Persona.Name;
                return $"{name}: {m.Text}";
            }));

            // Gemini 사후 분석 요청
            string postProcessSystemPrompt = $$"""
<role>월드 관리인 (대화 분석 시스템)</role>
<task>두 에이전트 간의 대화 내용을 분석하여 관계 변화 및 전파된 정보(믿음)를 기록하십시오.</task>

<context>
[대화 참여자]
- 에이전트 A: {{player.Persona.Name}} (ID: {{player.NumericId}})
- 에이전트 B: {{npc.Persona.Name}} (ID: {{npc.NumericId}})

[정보 및 신념 후보 목록]
{{string.Join("\n", npc.MemoryBox.Beliefs.Values.Where(b => b.Type != BeliefType.Core).Select(b => $"- [{b.BeliefId}] {b.SubjectId}에 관한 정보: \"{b.Content}\""))}}

[대화 원본]
<chat_log>
{{rawHistory}}
</chat_log>
</context>

<rules>
1. summary: 대화 요약을 3인칭 소설 기술처럼 작성하되, 수치(골드, 수치 스탯 등)는 배제하고 1문장으로 요약하십시오.
2. relationship_changes: 대화 내용을 바탕으로 서로에 대한 호감도(liking)와 신뢰도(trust) 변화량을 -10에서 +10 사이 정수값(delta)으로 산출하십시오. 친화적이면 +, 다툼/불신이 커지면 -입니다.
3. beliefs_shared: 대화 중 소문이나 특정 정보가 전파되었는지 분석하십시오.
   - belief_id: 위 [정보 및 신념 후보 목록] 중에서, 대화 중 실제로 전파(발설)된 정보의 '믿음 ID'를 찾아 기입하십시오. 매칭되는 정보가 없는 새로운 정보일 경우 빈 문자열("")로 기입하십시오.
   - subject: 정보의 대상이 된 인물의 AgentId (예: 'npc_kyle'). 대화에서 해당 대상이 직접 지목된 경우에만 추출하십시오.
   - content: 대화 중 발설된 정보의 핵심 요약 내용.
   - credibility_rating: 들려온 이야기에 대해 화자가 보인 신빙성 정도 (0 ~ 100).
   - speaker_id: 정보를 말한 화자의 AgentId.
4. 분석 결과는 오직 아래 지정된 JSON 포맷으로만 출력하십시오.
</rules>

<output_format>
{
  "summary": "에이전트 A와 B가 안부를 주고받으며 일상 대화를 나눴습니다.",
  "relationship_changes": {
    "liking_delta_a_to_b": 0,
    "trust_delta_a_to_b": 0,
    "liking_delta_b_to_a": 0,
    "trust_delta_b_to_a": 0
  },
  "beliefs_shared": []
}
</output_format>
""";

            var postRequest = new GeminiRequest(
                SystemInstruction: new Content("system", new List<Part> { new Part(postProcessSystemPrompt) }),
                Contents: new List<Content> { new Content("user", new List<Part> { new Part("분석 시작.") }) },
                GenerationConfig: new GenerationConfig(null, 2048, "application/json", null, new ThinkingConfig(ThinkingLevel.minimal))
            );

            string postResponse = await _apiService.SendMessageAsync(postRequest, ModelTier.FlashLite, cancellationToken);
            var analysis = LlmJsonParser.DeserializeSafe<ConversationAnalysis>(postResponse);

            string summary = "대화 분석에 실패했습니다.";
            if (analysis != null)
            {
                summary = analysis.Summary;
                var timestamp = DateTime.Now;

                // 대화 사건 자체를 Witnessed Belief로 저장
                player.MemoryBox.AddOrUpdateBelief(new Belief
                {
                    BeliefId = $"belief_dialogue_{Guid.NewGuid().ToString().Substring(0, 5)}",
                    SubjectId = player.AgentId,
                    Content = $"{npc.Persona.Name}와 대화함: {summary}",
                    Type = BeliefType.Witnessed,
                    Confidence = 1.0,
                    Salience = 1.0,
                    EmotionalCharge = 0.3,
                    AcquiredAt = timestamp
                });
                npc.MemoryBox.AddOrUpdateBelief(new Belief
                {
                    BeliefId = $"belief_dialogue_{Guid.NewGuid().ToString().Substring(0, 5)}",
                    SubjectId = npc.AgentId,
                    Content = $"{player.Persona.Name}와 대화함: {summary}",
                    Type = BeliefType.Witnessed,
                    Confidence = 1.0,
                    Salience = 1.0,
                    EmotionalCharge = 0.3,
                    AcquiredAt = timestamp
                });

                // 관계 갱신 및 이벤트 브로드캐스트
                var relPlayerToNpc = GetOrCreateRelationship(player, npc.AgentId);
                var relNpcToPlayer = GetOrCreateRelationship(npc, player.AgentId);

                int likingDeltaAToB = analysis.RelationshipChanges.LikingDeltaAToB;
                int trustDeltaAToB = analysis.RelationshipChanges.TrustDeltaAToB;
                int likingDeltaBToA = analysis.RelationshipChanges.LikingDeltaBToA;
                int trustDeltaBToA = analysis.RelationshipChanges.TrustDeltaBToA;

                relPlayerToNpc.Liking = Math.Clamp(relPlayerToNpc.Liking + likingDeltaAToB, -100, 100);
                relPlayerToNpc.Trust = Math.Clamp(relPlayerToNpc.Trust + trustDeltaAToB, 0, 100);
                RelationshipChangeTracker.TrackChange(player.NumericId, npc.NumericId, relPlayerToNpc.Liking, relPlayerToNpc.Trust);

                relNpcToPlayer.Liking = Math.Clamp(relNpcToPlayer.Liking + likingDeltaBToA, -100, 100);
                relNpcToPlayer.Trust = Math.Clamp(relNpcToPlayer.Trust + trustDeltaBToA, 0, 100);
                RelationshipChangeTracker.TrackChange(npc.NumericId, player.NumericId, relNpcToPlayer.Liking, relNpcToPlayer.Trust);

                // 관계 변동 브로드캐스트
                var relEventAToB = new WorldEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Relationship = new RelationshipEvent
                {
                    FromAgentId = player.NumericId,
                    ToAgentId = npc.NumericId,
                    NewLiking = relPlayerToNpc.Liking,
                    LikingDelta = likingDeltaAToB,
                    NewTrust = relPlayerToNpc.Trust,
                    TrustDelta = trustDeltaAToB
                }
                };
                await _broadcaster.BroadcastAsync(relEventAToB);

                var relEventBToA = new WorldEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Relationship = new RelationshipEvent
                {
                    FromAgentId = npc.NumericId,
                    ToAgentId = player.NumericId,
                    NewLiking = relNpcToPlayer.Liking,
                    LikingDelta = likingDeltaBToA,
                    NewTrust = relNpcToPlayer.Trust,
                    TrustDelta = trustDeltaBToA
                }
                };
                await _broadcaster.BroadcastAsync(relEventBToA);

                // 정보(믿음) 전파 처리
                if (analysis.BeliefsShared != null)
                {
                    foreach (var beliefElem in analysis.BeliefsShared)
                    {
                        string subject = beliefElem.Subject;
                        string content = beliefElem.Content;
                        string speakerId = beliefElem.SpeakerId;

                        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(content)) continue;

                        AgentInstance speaker = speakerId == player.AgentId ? player : npc;
                        AgentInstance listener = speakerId == player.AgentId ? npc : player;

                        Belief? originalBelief = null;

                        if (!string.IsNullOrWhiteSpace(beliefElem.BeliefId))
                        {
                            if (speaker.MemoryBox.Beliefs.TryGetValue(beliefElem.BeliefId, out var kb))
                            {
                                originalBelief = kb;
                            }
                        }

                        // Tier 2: 이번 세션에 정했던 공유 대상 정보와 비교
                        if (originalBelief == null && speaker.AgentId == npc.AgentId && !string.IsNullOrWhiteSpace(session.NpcBeliefIdToShare))
                        {
                            if (npc.MemoryBox.Beliefs.TryGetValue(session.NpcBeliefIdToShare, out var targetBeliefToShare) && targetBeliefToShare.SubjectId == subject)
                            {
                                originalBelief = targetBeliefToShare;
                            }
                        }

                        // Tier 3: 임베딩 유사도 기반 매칭
                        if (originalBelief == null)
                        {
                            var candidates = speaker.MemoryBox.Beliefs.Values
                                .Where(b => b.SubjectId == subject && b.Type != BeliefType.Core)
                                .ToList();

                            double maxSim = 0;
                            Belief? bestMatch = null;

                            var contentEmbedding = await _embeddingCache.GetOrComputeEmbeddingAsync(content, async t => await _apiService.GetEmbeddingAsync(t));

                            foreach (var cand in candidates)
                            {
                                if (cand.ContentEmbedding == null)
                                {
                                    cand.ContentEmbedding = await _embeddingCache.GetOrComputeEmbeddingAsync(cand.Content, async t => await _apiService.GetEmbeddingAsync(t));
                                }

                                double sim = EmbeddingCache.CosineSimilarity(contentEmbedding, cand.ContentEmbedding);
                                if (sim > maxSim)
                                {
                                    maxSim = sim;
                                    bestMatch = cand;
                                }
                            }

                            if (maxSim >= 0.82 && bestMatch != null)
                            {
                                originalBelief = bestMatch;
                            }
                        }

                        if (originalBelief == null)
                        {
                            originalBelief = new Belief
                            {
                                BeliefId = $"belief_{subject}_{Guid.NewGuid().ToString().Substring(0, 5)}",
                                SubjectId = subject,
                                Content = content,
                                Type = BeliefType.Heard,
                                Confidence = 0.7,
                                Salience = 1.0,
                                EmotionalCharge = 0.5,
                                SourceAgentId = speaker.AgentId,
                                AcquiredAt = DateTime.UtcNow
                            };
                            speaker.MemoryBox.AddOrUpdateBelief(originalBelief);
                        }

                        await _beliefEngine.ProcessBeliefSharingAsync(speaker, listener, originalBelief, content);

                        // 실시간 이벤트 브로드캐스트
                        bool isMutated = !originalBelief.Content.Trim().Equals(content.Trim(), StringComparison.OrdinalIgnoreCase);
                        var beliefEvent = new WorldEvent
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            BeliefShare = new BeliefShareEvent
                            {
                                SpeakerId = speaker.NumericId,
                                ListenerId = listener.NumericId,
                                SubjectId = AgentIdMapping.GetNumericId(originalBelief.SubjectId),
                                Content = content,
                                IsMutated = isMutated
                            }
                        };
                        await _broadcaster.BroadcastAsync(beliefEvent);
                    }
                }

                // 로그 및 파일 로깅
                string logMsg = $"대화 발생 ({player.Persona.Name} <-> {npc.Persona.Name}): {summary}\n" +
                                $"   관계 변화:\n" +
                                $"     * {player.Persona.Name} ➔ {npc.Persona.Name}: 호감도 {relPlayerToNpc.Liking} ({likingDeltaAToB:+#;-#;0}), 신뢰도 {relPlayerToNpc.Trust} ({trustDeltaAToB:+#;-#;0})\n" +
                                $"     * {npc.Persona.Name} ➔ {player.Persona.Name}: 호감도 {relNpcToPlayer.Liking} ({likingDeltaBToA:+#;-#;0}), 신뢰도 {relNpcToPlayer.Trust} ({trustDeltaBToA:+#;-#;0})";
                await _memoryLogger.LogMemoryEventAsync(logMsg);
            }

            // 대화 종료 이벤트 브로드캐스트
            var structuredLines = session.ConversationHistory.Select(m => new DialogueLine
            {
                SpeakerId = AgentIdMapping.GetNumericId(m.Role),
                SpeakerName = m.Role == player.AgentId ? player.Persona.Name : npc.Persona.Name,
                Text = m.Text
            }).ToList();

            var endEvent = new WorldEvent
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Dialogue = new DialogueEvent
                {
                    TaskId = session.SessionId,
                    AgentAId = player.NumericId,
                    AgentBId = npc.NumericId,
                    Location = LocationCoordinateRegistry.CreateLocationInfo(npc.Status.CurrentLocation),
                    IsStarted = false,
                    Summary = summary
                }
            };
            endEvent.Dialogue.Lines.AddRange(structuredLines);
            await _broadcaster.BroadcastAsync(endEvent);

            // Release lock
            player.Status.IsInConversation = false;
            npc.Status.IsInConversation = false;
            player.Status.Activity = "마을 둘러보기";
            npc.Status.Activity = "대기 중";

            // DB에 에이전트 상태 비동기 영구 저장 (Write-Behind)
            _ = Task.Run(() =>
            {
                try
                {
                    _persistence.UpsertAgent(player);
                    _persistence.UpsertAgent(npc);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PlayerDialogueManager DB Error] Failed to async save agents: {ex.Message}");
                }
            });

            return (true, summary);
        }

        private Relationship GetOrCreateRelationship(AgentInstance agent, string targetId)
        {
            if (!agent.RelationshipMap.TryGetValue(targetId, out var rel))
            {
                rel = new Relationship { TargetAgentId = targetId, Liking = 0, Trust = 50 };
                agent.RelationshipMap[targetId] = rel;
            }
            return rel;
        }

        /// <summary>
        /// 관계 감정, 와전 자극도, 미전파 우선순위를 반영한 Top-K 믿음(기억) 선별 헬퍼.
        /// </summary>
        private List<Belief> ComputeTopKBeliefs(AgentInstance speaker, AgentInstance listener, int k = 10)
        {
            _beliefEngine.DecayBeliefs(speaker, _beliefEngine.CurrentTick);
            _beliefEngine.DecayBeliefs(listener, _beliefEngine.CurrentTick);

            return speaker.MemoryBox.Beliefs.Values
                .Select(b => {
                    double score = b.Importance; // 기본 중요도 식 (Confidence * 0.4 + Salience * 0.35 + EmotionalCharge * 0.25)

                    // 당사자 관련 가중치 추가
                    if (b.SubjectId == listener.AgentId)
                        score += 0.15;

                    // 아직 발설하지 않은 신념 가중치 추가
                    if (!b.SharedWith.Contains(listener.AgentId))
                        score += 0.25;

                    // 감정 및 관계에 의한 가중치 추가
                    if (speaker.RelationshipMap.TryGetValue(b.SubjectId, out var subjectRel))
                        score += Math.Min(Math.Abs(subjectRel.Liking) / 250.0, 0.4);

                    // 와전 자극 가중치
                    score += Math.Min(b.MutationCount * 0.08, 0.25);

                    return new { Belief = b, Score = score };
                })
                .OrderByDescending(x => x.Score)
                .Take(k)
                .Select(x => x.Belief)
                .ToList();
        }

        public async Task CleanupIdleSessionsAsync(TimeSpan idleTimeout, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var idleSessions = _activeSessions.Values
                .Where(s => now - s.LastActiveAt > idleTimeout)
                .ToList();

            foreach (var session in idleSessions)
            {
                try
                {
                    Console.WriteLine($"[PlayerDialogueManager] Cleaning up idle session {session.SessionId} (Inactive since {session.LastActiveAt:u})");
                    await EndDialogueAsync(session.SessionId, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PlayerDialogueManager Error] Failed to cleanup idle session {session.SessionId}: {ex.Message}");
                }
            }
        }

        private string CleanResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return string.Empty;
            return response.Trim().Replace("\"", "");
        }
    }
