using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Helpers;
using MundusVivens.Prototype.Protos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services
{
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
        private readonly IGossipEngine _gossipEngine;
        private readonly IWorldEventBroadcaster _broadcaster;
        private readonly MemoryEventLogger _memoryLogger;
        private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
        private readonly IEmbeddingCache _embeddingCache;

        private readonly IPersistenceService _persistence;

        private readonly ConcurrentDictionary<ulong, PlayerDialogueSession> _activeSessions = new();

        public PlayerDialogueManager(
            IGeminiApiService apiService,
            IGossipEngine gossipEngine,
            IWorldEventBroadcaster broadcaster,
            MemoryEventLogger memoryLogger,
            Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
            IEmbeddingCache embeddingCache,
            IPersistenceService persistence)
        {
            _apiService = apiService;
            _gossipEngine = gossipEngine;
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
            var relevantEpisodes = npc.MemoryBox.EpisodicMemories
                .Where(e => (e.InvolvedAgentIds != null && e.InvolvedAgentIds.Contains(playerId)) || e.TargetName == player.Persona.Name)
                .Select(e => $"[{e.Timestamp:HH:mm}] {e.Summary}");

            string relevantMemoriesStr = string.Join("\n", relevantEpisodes);
            if (npc.MemoryBox.CoreMemories.Any())
            {
                relevantMemoriesStr += "\n[나의 평생 장기 기억]\n" + string.Join("\n", npc.MemoryBox.CoreMemories.Select(c => $"- {c.Content}"));
            }

            if (string.IsNullOrWhiteSpace(relevantMemoriesStr))
            {
                relevantMemoriesStr = "플레이어에 대한 특별한 과거 기억이 없습니다.";
            }

            // Top-K 기억을 먼저 확정한 뒤, 그 안에서 전파 소문을 선택
            var sortedGossips = ComputeTopKGossips(npc, player);

            // 소문 공유 확인 (기억 안에서 선택)
            var gossipToShare = _gossipEngine.SelectGossipToShare(npc, player, sortedGossips);
            session.NpcGossipIdToShare = gossipToShare?.Gossip.GossipId;

            string gossipSnippet = "없음 (평범하게 첫 인사를 건네십시오)";
            if (gossipToShare != null)
            {
                gossipSnippet = $"[비밀 소문 폭로 지시] 대상: {gossipToShare.Gossip.Subject}, 소문 내용: \"{gossipToShare.Gossip.Content}\"\n" +
                    "지시: 첫 인사 대화 중 자연스럽게 플레이어에게 이 소문을 흘리거나 질문해 보십시오. (예: '마침 잘 왔군, 혹시 카일 소식 들었나?') 소문의 대상 인물 실명을 언급하십시오.";
            }

            string knownGossipsStr = string.Join("\n", sortedGossips.Select(kg => $"- {kg.Gossip.Content} (확신도: {kg.SubjectiveBelief:F2})"));
            if (string.IsNullOrWhiteSpace(knownGossipsStr))
            {
                knownGossipsStr = "알고 있는 특별한 소문이 없습니다.";
            }

            Console.WriteLine($"📋 [소문 압축] {npc.Persona.Name}: {npc.KnownGossips.Count} -> {sortedGossips.Count}개 선별 (플레이어 대화 시작)");

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
- 상대에 대한 나의 태도: 호감도 {{rel.Liking}}/100, 신뢰도 {{rel.Trust}}/100

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
            var relevantEpisodes = npc.MemoryBox.EpisodicMemories
                .Where(e => (e.InvolvedAgentIds != null && e.InvolvedAgentIds.Contains(session.PlayerId)) || e.TargetName == player.Persona.Name)
                .Select(e => $"[{e.Timestamp:HH:mm}] {e.Summary}");

            string relevantMemoriesStr = string.Join("\n", relevantEpisodes);
            if (npc.MemoryBox.CoreMemories.Any())
            {
                relevantMemoriesStr += "\n[나의 평생 장기 기억]\n" + string.Join("\n", npc.MemoryBox.CoreMemories.Select(c => $"- {c.Content}"));
            }

            if (string.IsNullOrWhiteSpace(relevantMemoriesStr))
            {
                relevantMemoriesStr = "플레이어에 대한 특별한 과거 기억이 없습니다.";
            }

            // Top-K 기억 확정 후 그 안에서 전파 소문 선택
            var sortedGossips = ComputeTopKGossips(npc, player);

            // 소문 공유 확인 (세션 시작 시 선택된 소문 유지)
            KnownGossip? gossipToShare = null;
            if (!string.IsNullOrWhiteSpace(session.NpcGossipIdToShare))
            {
                npc.KnownGossips.TryGetValue(session.NpcGossipIdToShare, out gossipToShare);
            }

            string gossipSnippet = "없음 (플레이어의 질문에 성실히 대답하십시오)";
            if (gossipToShare != null)
            {
                gossipSnippet = $"[비밀 소문 폭로 지시] 대상: {gossipToShare.Gossip.Subject}, 소문 내용: \"{gossipToShare.Gossip.Content}\"\n" +
                    "지시: 기회가 된다면 대화 흐름 중 자연스럽게 플레이어에게 이 소문을 흘리십시오. 소문의 대상 인물 실명을 언급하십시오.";
            }

            string knownGossipsStr = string.Join("\n", sortedGossips.Select(kg => $"- {kg.Gossip.Content} (확신도: {kg.SubjectiveBelief:F2})"));
            if (string.IsNullOrWhiteSpace(knownGossipsStr))
            {
                knownGossipsStr = "알고 있는 특별한 소문이 없습니다.";
            }

            Console.WriteLine($"📋 [소문 압축] {npc.Persona.Name}: {npc.KnownGossips.Count} -> {sortedGossips.Count}개 선별 (플레이어 메시지 전송)");

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
- 상대에 대한 나의 태도: 호감도 {{rel.Liking}}/100, 신뢰도 {{rel.Trust}}/100

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
<task>두 에이전트 간의 대화 내용을 분석하여 관계 변화 및 전파된 소문 정보를 기록하십시오.</task>

<context>
[대화 참여자]
- 에이전트 A: {{player.Persona.Name}} (ID: {{player.NumericId}})
- 에이전트 B: {{npc.Persona.Name}} (ID: {{npc.NumericId}})

[소문 후보 목록]
{{string.Join("\n", npc.KnownGossips.Values.Select(kg => $"- [{kg.Gossip.GossipId}] {kg.Gossip.Subject}에 관한 소문: \"{kg.Gossip.Content}\""))}}

[대화 원본]
<chat_log>
{{rawHistory}}
</chat_log>
</context>

<rules>
1. summary: 대화 요약을 3인칭 소설 기술처럼 작성하되, 수치(골드, 수치 스탯 등)는 배제하고 1문장으로 요약하십시오.
2. relationship_changes: 대화 내용을 바탕으로 서로에 대한 호감도(liking)와 신뢰도(trust) 변화량을 -10에서 +10 사이 정수값(delta)으로 산출하십시오. 친화적이면 +, 다툼/불신이 커지면 -입니다.
3. gossips_exchanged: 대화 중 소문이나 특정 정보가 전파되었는지 분석하십시오.
   - gossip_id: 위 [소문 후보 목록] 중에서, 대화 중 실제로 전파(발설)된 소문의 '소문 ID'를 찾아 기입하십시오. 매칭되는 소문이 없는 새로운 소문일 경우 빈 문자열("")로 기입하십시오.
   - subject: 소문의 대상이 된 인물의 AgentId (예: 'npc_kyle'). 대화에서 해당 대상이 직접 지목된 경우에만 추출하십시오.
   - content: 대화 중 발설된 소문의 핵심 요약 내용.
   - credibility_rating: 들려온 이야기에 대해 화자가 보인 신빙성 정도 (0 ~ 100).
   - speaker_id: 소문을 말한 화자의 AgentId.
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
  "gossips_exchanged": []
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

                // 에피소드 저장
                player.MemoryBox.AddEpisode(new Episode
                {
                    Timestamp = timestamp,
                    TargetName = npc.Persona.Name,
                    Summary = summary,
                    InvolvedAgentIds = new List<string> { player.AgentId, npc.AgentId }
                });
                npc.MemoryBox.AddEpisode(new Episode
                {
                    Timestamp = timestamp,
                    TargetName = player.Persona.Name,
                    Summary = summary,
                    InvolvedAgentIds = new List<string> { player.AgentId, npc.AgentId }
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
                        NewAffinity = relPlayerToNpc.Liking,
                        AffinityDelta = likingDeltaAToB,
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
                        NewAffinity = relNpcToPlayer.Liking,
                        AffinityDelta = likingDeltaBToA,
                        NewTrust = relNpcToPlayer.Trust,
                        TrustDelta = trustDeltaBToA
                    }
                };
                await _broadcaster.BroadcastAsync(relEventBToA);

                // 소문 전파 처리
                if (analysis.GossipsExchanged != null)
                {
                    foreach (var gossipElem in analysis.GossipsExchanged)
                    {
                        string subject = gossipElem.Subject;
                        string content = gossipElem.Content;
                        string speakerId = gossipElem.SpeakerId;

                        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(content)) continue;

                        AgentInstance speaker = speakerId == player.AgentId ? player : npc;
                        AgentInstance listener = speakerId == player.AgentId ? npc : player;

                        GossipItem? originalGossip = null;

                        if (!string.IsNullOrWhiteSpace(gossipElem.GossipId))
                        {
                            if (speaker.KnownGossips.TryGetValue(gossipElem.GossipId, out var knownGossip))
                            {
                                originalGossip = knownGossip.Gossip;
                            }
                        }

                        // Tier 2: LLM의 ID 매칭 실패 시, 이번 대화 시작 시 공유하기로 지정되었던 소문과 비교
                        if (originalGossip == null && speaker.AgentId == npc.AgentId && !string.IsNullOrWhiteSpace(session.NpcGossipIdToShare))
                        {
                            if (npc.KnownGossips.TryGetValue(session.NpcGossipIdToShare, out var targetGossipToShare) && targetGossipToShare.Gossip.Subject == subject)
                            {
                                originalGossip = targetGossipToShare.Gossip;
                            }
                        }

                        // Tier 3 (신규): 임베딩 유사도 기반 매칭
                        if (originalGossip == null)
                        {
                            var candidates = speaker.KnownGossips.Values
                                .Where(kg => kg.Gossip.Subject == subject)
                                .ToList();

                            double maxSim = 0;
                            GossipItem? bestMatch = null;

                            var contentEmbedding = await _embeddingCache.GetOrComputeEmbeddingAsync(content, async t => await _apiService.GetEmbeddingAsync(t));

                            foreach (var cand in candidates)
                            {
                                if (cand.Gossip.ContentEmbedding == null)
                                {
                                    cand.Gossip.ContentEmbedding = await _embeddingCache.GetOrComputeEmbeddingAsync(cand.Gossip.Content, async t => await _apiService.GetEmbeddingAsync(t));
                                }

                                double sim = EmbeddingCache.CosineSimilarity(contentEmbedding, cand.Gossip.ContentEmbedding);
                                if (sim > maxSim)
                                {
                                    maxSim = sim;
                                    bestMatch = cand.Gossip;
                                }
                            }

                            if (maxSim >= 0.82 && bestMatch != null)
                            {
                                originalGossip = bestMatch;
                                Console.WriteLine($"[PlayerDialogueManager] 🧬 [임베딩 매칭 성공] 추출된 소문과 화자의 기존 소문 유사도 {maxSim:F3} 매칭 성공 (ID: {originalGossip.GossipId})");
                            }
                        }



                        if (originalGossip == null)
                        {
                            originalGossip = new GossipItem
                            {
                                GossipId = $"gossip_{subject}_{Guid.NewGuid().ToString().Substring(0, 5)}",
                                Subject = subject,
                                Content = content,
                                SourceAgentId = speaker.AgentId,
                                BaseCredibility = 70
                            };
                            speaker.KnownGossips[originalGossip.GossipId] = new KnownGossip
                            {
                                Gossip = originalGossip,
                                SubjectiveBelief = 1.0,
                                HasSharedWithOthers = true
                            };
                        }

                        await _gossipEngine.ProcessGossipSharingAsync(speaker, listener, originalGossip, content);

                        // 소문 전파 실시간 이벤트 브로드캐스트
                        bool isMutated = !originalGossip.Content.Trim().Equals(content.Trim(), StringComparison.OrdinalIgnoreCase);
                        var gossipEvent = new WorldEvent
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Gossip = new GossipEvent
                            {
                                SpeakerId = speaker.NumericId,
                                ListenerId = listener.NumericId,
                                SubjectId = AgentIdMapping.GetNumericId(originalGossip.Subject),
                                GossipContent = content,
                                IsMutated = isMutated
                            }
                        };
                        await _broadcaster.BroadcastAsync(gossipEvent);

                        // 에피소드 귀속
                        string subjectName = (subject == player.AgentId) ? player.Persona.Name : ((subject == npc.AgentId) ? npc.Persona.Name : subject);
                        listener.MemoryBox.AddEpisode(new Episode
                        {
                            Timestamp = DateTime.Now,
                            TargetName = speaker.Persona.Name,
                            Summary = $"{speaker.Persona.Name}(으)로부터 {subjectName}에 대한 소문(\"{content}\")을 들었습니다.",
                            InvolvedAgentIds = new List<string> { speaker.AgentId, listener.AgentId, subject }
                        });
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
        /// 관계 감정, 와전 자극도, 미전파 우선순위를 반영한 Top-K 소문 기억 선별 헬퍼.
        /// DialogueOrchestrator의 ComputeTopKGossips와 동일한 점수 공식을 사용합니다.
        /// </summary>
        private List<KnownGossip> ComputeTopKGossips(AgentInstance speaker, AgentInstance listener, int k = 3)
        {
            _gossipEngine.DecayAgentGossips(speaker, _gossipEngine.CurrentTick);
            _gossipEngine.DecayAgentGossips(listener, _gossipEngine.CurrentTick);

            return speaker.KnownGossips.Values
                .Select(kg => {
                    double score = kg.SubjectiveBelief; // 기본 확신도 (0.0 ~ 1.0)

                    // 당사자 인식 가중치: 대화 상대에 대한 소문을 살짝 의식 (+0.15)
                    if (kg.Gossip.Subject == listener.AgentId)
                        score += 0.15;

                    // 미전파 소문 가중치: 아직 남에게 말하지 않은 따끈한 소문 우선 (+0.25)
                    if (!kg.HasSharedWithOthers)
                        score += 0.25;

                    // 감정 연동 가중치: 소문 대상에 대한 감정이 강할수록 기억에 잘 남음 (max +0.4)
                    if (speaker.RelationshipMap.TryGetValue(kg.Gossip.Subject, out var subjectRel))
                        score += Math.Min(Math.Abs(subjectRel.Liking) / 250.0, 0.4);

                    // 와전 자극도 가중치: 여러 입을 거친 자극적 소문이 더 기억에 남음 (max +0.25)
                    score += Math.Min(kg.Gossip.MutationCount * 0.08, 0.25);

                    // 시간 신선도 가중치: 최근에 들은 소문이 더 잘 떠오름 (max +0.2)
                    double secondsSinceAcquired = (DateTime.UtcNow - kg.AcquiredAt).TotalSeconds;
                    score += Math.Max(0.0, (1.0 - secondsSinceAcquired / 1000.0) * 0.2);

                    return new { Gossip = kg, Score = score };
                })
                .OrderByDescending(x => x.Score)
                .Take(k)
                .Select(x => x.Gossip)
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
}
