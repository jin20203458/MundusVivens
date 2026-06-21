using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Helpers;
using MundusVivens.Prototype.Protos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public class DialogueResult
{
    public string Summary { get; set; } = string.Empty;
    public List<string> DialogueLines { get; set; } = new();
    public List<DialogueLine> StructuredLines { get; set; } = new();
    public List<AgentEmotionUpdate> EmotionUpdates { get; set; } = new(); // 🆕 감정 업데이트 목록 추가
}

public interface IDialogueOrchestrator
{
    Task<DialogueResult> RunConversationAsync(AgentInstance agentA, AgentInstance agentB, string taskId = "", CancellationToken cancellationToken = default);
}

public class DialogueOrchestrator : IDialogueOrchestrator
{
    private readonly IGeminiApiService _apiService;
    private readonly IGossipEngine _gossipEngine;
    private readonly MemoryEventLogger _memoryLogger;
    private readonly IWorldEventBroadcaster _broadcaster;
    private readonly IEmbeddingCache _embeddingCache;

    public DialogueOrchestrator(
        IGeminiApiService apiService,
        IGossipEngine gossipEngine,
        MemoryEventLogger memoryLogger,
        IWorldEventBroadcaster broadcaster,
        IEmbeddingCache embeddingCache)
    {
        _apiService = apiService;
        _gossipEngine = gossipEngine;
        _memoryLogger = memoryLogger;
        _broadcaster = broadcaster;
        _embeddingCache = embeddingCache;
    }

    public async Task<DialogueResult> RunConversationAsync(AgentInstance agentA, AgentInstance agentB, string taskId = "", CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"\n=======================================================");
        Console.WriteLine($"💬 대화 시작: {agentA.Persona.Name} ({agentA.Persona.Job})  ◀ ▷  {agentB.Persona.Name} ({agentB.Persona.Job})");
        Console.WriteLine($"📍 위치: {agentA.Status.CurrentLocation}");
        
        var relAToB = GetOrCreateRelationship(agentA, agentB.AgentId);
        var relBToA = GetOrCreateRelationship(agentB, agentA.AgentId);
        Console.WriteLine($"   * {agentA.Persona.Name}의 태도: 호감도 {relAToB.Liking}, 신뢰도 {relAToB.Trust}");
        Console.WriteLine($"   * {agentB.Persona.Name}의 태도: 호감도 {relBToA.Liking}, 신뢰도 {relBToA.Trust}");
        Console.WriteLine($"=======================================================\n");

        // Top-K 기억을 먼저 확정한 뒤, 그 안에서 전파 소문을 선택
        var topKForA = ComputeTopKGossips(agentA, agentB);
        var topKForB = ComputeTopKGossips(agentB, agentA);

        var gossipToShareByA = _gossipEngine.SelectGossipToShare(agentA, agentB, topKForA);
        var gossipToShareByB = _gossipEngine.SelectGossipToShare(agentB, agentA, topKForB);

        Console.WriteLine($"📋 [소문 압축] {agentA.Persona.Name}: {agentA.KnownGossips.Count} -> {topKForA.Count}개 선별");
        Console.WriteLine($"📋 [소문 압축] {agentB.Persona.Name}: {agentB.KnownGossips.Count} -> {topKForB.Count}개 선별");

        agentA.MemoryBox.ActiveConversation.Clear();
        agentB.MemoryBox.ActiveConversation.Clear();

        var conversationHistory = new List<ChatMessage>();
        
        AgentInstance currentSpeaker = agentA;
        AgentInstance currentListener = agentB;
        
        int totalTurns = 4;
        
        for (int turn = 0; turn < totalTurns; turn++)
        {
            var rel = GetOrCreateRelationship(currentSpeaker, currentListener.AgentId);
            var relevantEpisodes = currentSpeaker.MemoryBox.EpisodicMemories
                .Where(e => (e.InvolvedAgentIds != null && e.InvolvedAgentIds.Contains(currentListener.AgentId)) ||
                            e.TargetName == currentListener.Persona.Name || 
                            e.TargetName == currentListener.AgentId)
                .Select(e => $"[{e.Timestamp:HH:mm}] {e.Summary}");
            
            string relevantMemoriesStr = string.Join("\n", relevantEpisodes);
            if (currentSpeaker.MemoryBox.CoreMemories.Any())
            {
                relevantMemoriesStr += "\n[나의 평생 장기 기억]\n" + string.Join("\n", currentSpeaker.MemoryBox.CoreMemories.Select(c => $"- {c.Content}"));
            }

            if (string.IsNullOrWhiteSpace(relevantMemoriesStr))
            {
                relevantMemoriesStr = "상대방에 대한 특별한 과거 기억이 없습니다.";
            }

            var currentGossipToShare = currentSpeaker == agentA ? gossipToShareByA : gossipToShareByB;
            string gossipSnippet = "없음 (평범한 일상 대화를 이어가십시오)";
            if (currentGossipToShare != null)
            {
                gossipSnippet = $"[비밀 소문 폭로 지시] 대상: {currentGossipToShare.Gossip.Subject}, 소문 내용: \"{currentGossipToShare.Gossip.Content}\"\n" +
                    "지시: 대화 흐름 중 상대방에게 이 소문을 소문의 대상 실명을 직접 언급하며 자연스럽게 흘리거나 폭로하십시오. 반드시 해당 인물의 행동을 이야기해야 합니다.";
            }

            // 사전 계산된 Top-K 기억 목록 사용
            var sortedGossips = currentSpeaker == agentA ? topKForA : topKForB;

            string knownGossipsStr = string.Join("\n", sortedGossips.Select(kg => $"- {kg.Gossip.Content} (확신도: {kg.SubjectiveBelief:F2})"));
            if (string.IsNullOrWhiteSpace(knownGossipsStr))
            {
                knownGossipsStr = "알고 있는 특별한 소문이 없습니다.";
            }

            string reIgnitionPrompt = "";
            if (rel.Liking <= -15)
            {
                reIgnitionPrompt = "\n[🚨 감정 재점화(Emotion Re-ignition) 경보]\n" +
                    "- 이 캐릭터는 당신에게 해를 입혔거나 적대 관계입니다. 비록 현재 당신의 표면적 감정 상태가 '평온함' 등 차분하더라도, 대화가 시작되면 과거의 원한, 갈등, 앙금이 마음 깊은 곳에서 재점화되어야 합니다.\n" +
                    "- 따라서 대화에서 과거에 있었던 분쟁이나 마음에 들지 않았던 일화를 직접 들춰내며 쌀쌀맞고 까칠한 태도(분노, 의심, 경계)로 대화를 시작하거나 리드하십시오.\n";
            }

            string systemPrompt = $@"당신은 가상 세계 시뮬레이션의 NPC [{currentSpeaker.Persona.Name}]입니다. 주어진 페르소나와 상대방에 대한 기억을 바탕으로 대답하십시오.

[내 페르소나]
- 이름/직업: {currentSpeaker.Persona.Name} / {currentSpeaker.Persona.Job}
- 성격/말투: {currentSpeaker.Persona.ToneStyle}
- 배경 이야기: {currentSpeaker.Persona.Backstory}
- 핵심 가치관: {currentSpeaker.Persona.CoreValues}

[대화 상대방 정보]
- 이름/직업: {currentListener.Persona.Name} / {currentListener.Persona.Job}
- 상대에 대한 나의 태도: 호감도 {rel.Liking}/100, 신뢰도 {rel.Trust}/100

[내가 알고 있는 소문 목록 (최대 3개 선별)]
{knownGossipsStr}

[기억 및 상황 맥락]
<relevant_memories>
{relevantMemoriesStr}
</relevant_memories>

<current_situation>
- 현재 위치: {currentSpeaker.Status.CurrentLocation}
- 나의 감정 상태: {currentSpeaker.Status.Emotion}
- 나의 행동 상태: {currentSpeaker.Status.Activity}
- 추가 화두: {gossipSnippet}
</current_situation>
{reIgnitionPrompt}

[대화 규칙]
1. AI 메타성 해설이나 지문, 상황 설명을 절대 적지 마십시오. 오직 대사만 출력하십시오.
2. 따옴표(\"" \"")를 사용하여 대사를 감싸서 말하십시오. 독백이 필요할 시 「 」를 사용하십시오.
3. 주어진 말투와 감정, 상대방에 대한 호감도를 고려하여 캐릭터를 실감나게 연기하십시오.
4. 한 번의 호출에 1~2문장의 간결한 대사만 출력하십시오.
5. 마크다운이나 별표(*) 같은 특수 꾸밈 기호를 본문에 포함하지 마십시오.
";

            var apiContents = new List<Content>();
            
            // 대화 시작 지점이거나 agentA(최초 발화자)의 턴일 때, 대화 순서가 user로 시작할 수 있도록 트리거 추가
            if (currentSpeaker.AgentId == agentA.AgentId)
            {
                apiContents.Add(new Content("user", new List<Part> { 
                    new Part($"[System] 당신({agentA.Persona.Name})은 상대방({agentB.Persona.Name})을 만났습니다. 자연스럽게 대화를 시작하십시오.") 
                }));
            }

            foreach (var msg in conversationHistory)
            {
                string apiRole = msg.Role == currentSpeaker.AgentId ? "model" : "user";
                apiContents.Add(new Content(apiRole, new List<Part> { new Part(msg.Text) }));
            }

            var request = new GeminiRequest(
                SystemInstruction: new Content("system", new List<Part> { new Part(systemPrompt) }),
                Contents: apiContents,
                SafetySettings: new List<SafetySetting> { new SafetySetting("HARM_CATEGORY_HARASSMENT", BlockThreshold.BLOCK_NONE) },
                GenerationConfig: new GenerationConfig(null, 4000, "text/plain", null, new ThinkingConfig(ThinkingLevel.minimal))
            );

            string dialogueResponse = await _apiService.SendMessageAsync(request, ModelTier.Flash35, cancellationToken);
            dialogueResponse = CleanResponse(dialogueResponse);

            Console.WriteLine($"💬 \x1b[36m{currentSpeaker.Persona.Name}\x1b[0m: {dialogueResponse}");
            
            var chatMessage = new ChatMessage(currentSpeaker.AgentId, dialogueResponse);
            conversationHistory.Add(chatMessage);

            // 실시간 대사 브로드캐스트
            if (!string.IsNullOrEmpty(taskId))
            {
                var liveEvent = new WorldEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Dialogue = new DialogueEvent
                    {
                        TaskId = taskId,
                        AgentAId = agentA.AgentId,
                        AgentBId = agentB.AgentId,
                        Location = agentA.Status.CurrentLocation,
                        IsStarted = true
                    }
                };
                liveEvent.Dialogue.Lines.Add(new DialogueLine
                {
                    SpeakerId = currentSpeaker.AgentId,
                    SpeakerName = currentSpeaker.Persona.Name,
                    Text = dialogueResponse
                });
                await _broadcaster.BroadcastAsync(liveEvent);
            }

            var temp = currentSpeaker;
            currentSpeaker = currentListener;
            currentListener = temp;

            await Task.Delay(1000, cancellationToken);
        }

        Console.WriteLine($"\n-------------------------------------------------------");
        Console.WriteLine($"⚙️  대화 정리 및 사후 분석 중...");
        
        string chatLog = string.Join("\n", conversationHistory.Select(m => {
            string name = m.Role == agentA.AgentId ? agentA.Persona.Name : agentB.Persona.Name;
            return $"{name}: {m.Text}";
        }));

        // 후보 소문 목록 구성 (화자 A와 B가 알고 있는 모든 소문 목록)
        var candidateGossips = new List<string>();
        foreach (var kg in agentA.KnownGossips.Values)
        {
            candidateGossips.Add($"- 소문 ID: {kg.Gossip.GossipId} (대상 AgentId: {kg.Gossip.Subject}, 소문 내용: \"{kg.Gossip.Content}\")");
        }
        foreach (var kg in agentB.KnownGossips.Values)
        {
            if (!agentA.KnownGossips.ContainsKey(kg.Gossip.GossipId))
            {
                candidateGossips.Add($"- 소문 ID: {kg.Gossip.GossipId} (대상 AgentId: {kg.Gossip.Subject}, 소문 내용: \"{kg.Gossip.Content}\")");
            }
        }
        string candidatesStr = candidateGossips.Any() ? string.Join("\n", candidateGossips) : "알려진 소문 없음";

        string postProcessSystemPrompt = $@"당신은 두 NPC 간의 대화 내용을 분석하고 관계 변화, 전파된 소문 및 대화 후 감정 변화를 기록하는 월드 관리 시스템입니다.
다음 대화를 객관적으로 분석하여 지정된 JSON 구조로만 출력하십시오. 절대 ```json 과 같은 마크다운 코드 블록이나 추가 문장을 붙이지 말고 순수 JSON만 반환하십시오.

[대화 참여자]
- 에이전트 A: {agentA.Persona.Name} (ID: {agentA.AgentId})
- 에이전트 B: {agentB.Persona.Name} (ID: {agentB.AgentId})

[대화 원본]
<chat_log>
{chatLog}
</chat_log>

[소문 후보 목록 (Gossip Candidates)]
대화 시작 시점 기준 두 에이전트가 보유하고 있던 소문 풀입니다:
{candidatesStr}

[분석 규칙]
1. summary: 대화 요약을 3인칭 소설 기술처럼 작성하되, 수치(골드, 수치 스탯 등)는 배제하고 1문장으로 요약하십시오.
2. relationship_changes: 대화 내용을 바탕으로 서로에 대한 호감도(liking)와 신뢰도(trust) 변화량을 -10에서 +10 사이 정수값(delta)으로 산출하십시오. 친화적이면 +, 다툼/불신이 커지면 -입니다.
3. gossips_exchanged: 대화 중 소문이나 특정 정보가 전파되었는지 분석하십시오.
   - gossip_id: 위 [소문 후보 목록] 중에서, 대화 중 실제로 전파(발설)된 소문의 '소문 ID'를 찾아 정확히 기입하십시오. 만약 후보 목록에 매칭되는 소문이 없는 새로운 소문일 경우, 임의로 ID를 생성하지 말고 빈 문자열("""")로 두십시오.
   - subject: 소문의 대상이 된 인물의 AgentId (예: 'npc_kyle' 또는 'npc_bart' 또는 'npc_eva'). 대화에서 해당 대상이 직접 지목된 경우에만 추출하십시오.
   - content: 대화 중 발설된 소문의 핵심 요약 내용 (예: '성물을 훔쳤다')
   - credibility_rating: 들려온 이야기에 대해 화자가 보인 신빙성 정도 (0 ~ 100)
   - speaker_id: 소문을 말한 화자의 AgentId
4. emotion_updates: 대화 후 각 에이전트가 느낀 새로운 감정 상태(단어 하나, 예: '평온함', '유쾌함', '분노', '슬픔', '당황함', '어색함', '의심' 등)를 분석하여 기록하십시오.
   - agent_id: 감정이 변한 에이전트의 ID
   - new_emotion: 대화 내용을 반영한 최종 감정 상태 단어 하나

[출력 포맷]
{{
  ""summary"": ""에이전트 A와 B가 안부를 주고받으며 일상 대화를 나눴습니다."",
  ""relationship_changes"": {{
    ""liking_delta_a_to_b"": 0,
    ""trust_delta_a_to_b"": 0,
    ""liking_delta_b_to_a"": 0,
    ""trust_delta_b_to_a"": 0
  }},
  ""gossips_exchanged"": [],
  ""emotion_updates"": [
    {{ ""agent_id"": ""{agentA.AgentId}"", ""new_emotion"": ""평온함"" }},
    {{ ""agent_id"": ""{agentB.AgentId}"", ""new_emotion"": ""유쾌함"" }}
  ]
}}
";

        var postRequest = new GeminiRequest(
            SystemInstruction: new Content("system", new List<Part> { new Part(postProcessSystemPrompt) }),
            Contents: new List<Content> { new Content("user", new List<Part> { new Part("분석 시작.") }) },
            GenerationConfig: new GenerationConfig(null, 2048, "application/json", null, new ThinkingConfig(ThinkingLevel.minimal))
        );

        string postResponse = await _apiService.SendMessageAsync(postRequest, ModelTier.FlashLite, cancellationToken);
        var analysis = LlmJsonParser.DeserializeSafe<ConversationAnalysis>(postResponse);

        var emotionUpdatesList = new List<AgentEmotionUpdate>();

        if (analysis != null)
        {
            // 감정 업데이트 파싱 및 C# 서버측 상태 동기화
            if (analysis.EmotionUpdates != null)
            {
                foreach (var emUpdate in analysis.EmotionUpdates)
                {
                    if (string.IsNullOrWhiteSpace(emUpdate.AgentId) || string.IsNullOrWhiteSpace(emUpdate.NewEmotion)) continue;

                    emotionUpdatesList.Add(new AgentEmotionUpdate
                    {
                        AgentId = emUpdate.AgentId,
                        NewEmotion = emUpdate.NewEmotion
                    });

                    if (emUpdate.AgentId == agentA.AgentId)
                    {
                        agentA.Status.Emotion = emUpdate.NewEmotion;
                        Console.WriteLine($"🎭 감정 업데이트: {agentA.Persona.Name} ➔ {emUpdate.NewEmotion}");
                    }
                    else if (emUpdate.AgentId == agentB.AgentId)
                    {
                        agentB.Status.Emotion = emUpdate.NewEmotion;
                        Console.WriteLine($"🎭 감정 업데이트: {agentB.Persona.Name} ➔ {emUpdate.NewEmotion}");
                    }
                }
            }

            string summary = analysis.Summary;
            var timestamp = DateTime.Now;

            agentA.MemoryBox.AddEpisode(new Episode 
            { 
                Timestamp = timestamp, 
                TargetName = agentB.Persona.Name, 
                Summary = summary,
                InvolvedAgentIds = new List<string> { agentA.AgentId, agentB.AgentId }
            });
            agentB.MemoryBox.AddEpisode(new Episode 
            { 
                Timestamp = timestamp, 
                TargetName = agentA.Persona.Name, 
                Summary = summary,
                InvolvedAgentIds = new List<string> { agentA.AgentId, agentB.AgentId }
            });

            // 관계 업데이트
            int likingDeltaAToB = analysis.RelationshipChanges.LikingDeltaAToB;
            int trustDeltaAToB = analysis.RelationshipChanges.TrustDeltaAToB;
            int likingDeltaBToA = analysis.RelationshipChanges.LikingDeltaBToA;
            int trustDeltaBToA = analysis.RelationshipChanges.TrustDeltaBToA;

            relAToB.Liking = Math.Clamp(relAToB.Liking + likingDeltaAToB, -100, 100);
            relAToB.Trust = Math.Clamp(relAToB.Trust + trustDeltaAToB, 0, 100);

            relBToA.Liking = Math.Clamp(relBToA.Liking + likingDeltaBToA, -100, 100);
            relBToA.Trust = Math.Clamp(relBToA.Trust + trustDeltaBToA, 0, 100);

            Console.WriteLine($"📈 관계 갱신 완료:");
            Console.WriteLine($"   * {agentA.Persona.Name} ➔ {agentB.Persona.Name}: 호감도 {relAToB.Liking} ({likingDeltaAToB:+#;-#;0}), 신뢰도 {relAToB.Trust} ({trustDeltaAToB:+#;-#;0})");
            Console.WriteLine($"   * {agentB.Persona.Name} ➔ {agentA.Persona.Name}: 호감도 {relBToA.Liking} ({likingDeltaBToA:+#;-#;0}), 신뢰도 {relBToA.Trust} ({trustDeltaBToA:+#;-#;0})");

            // 관계 변동 실시간 이벤트 브로드캐스트
            var relEventAToB = new WorldEvent
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Relationship = new RelationshipEvent
                {
                    FromAgentId = agentA.AgentId,
                    ToAgentId = agentB.AgentId,
                    NewAffinity = relAToB.Liking,
                    AffinityDelta = likingDeltaAToB,
                    NewTrust = relAToB.Trust,
                    TrustDelta = trustDeltaAToB
                }
            };
            await _broadcaster.BroadcastAsync(relEventAToB);

            var relEventBToA = new WorldEvent
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Relationship = new RelationshipEvent
                {
                    FromAgentId = agentB.AgentId,
                    ToAgentId = agentA.AgentId,
                    NewAffinity = relBToA.Liking,
                    AffinityDelta = likingDeltaBToA,
                    NewTrust = relBToA.Trust,
                    TrustDelta = trustDeltaBToA
                }
            };
            await _broadcaster.BroadcastAsync(relEventBToA);

            // 메모리 이벤트 로깅
            string logMsg = $"대화 발생 ({agentA.Persona.Name} <-> {agentB.Persona.Name}): {summary}\n" +
                            $"   관계 변화:\n" +
                            $"     * {agentA.Persona.Name} ➔ {agentB.Persona.Name}: 호감도 {relAToB.Liking} ({likingDeltaAToB:+#;-#;0}), 신뢰도 {relAToB.Trust} ({trustDeltaAToB:+#;-#;0})\n" +
                            $"     * {agentB.Persona.Name} ➔ {agentA.Persona.Name}: 호감도 {relBToA.Liking} ({likingDeltaBToA:+#;-#;0}), 신뢰도 {relBToA.Trust} ({trustDeltaBToA:+#;-#;0})";
            await _memoryLogger.LogMemoryEventAsync(logMsg);

            if (analysis.GossipsExchanged != null)
            {
                foreach (var gossipElem in analysis.GossipsExchanged)
                {
                    string subject = gossipElem.Subject;
                    string content = gossipElem.Content;
                    string speakerId = gossipElem.SpeakerId;

                    if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(content)) continue;

                    AgentInstance speaker = speakerId == agentA.AgentId ? agentA : agentB;
                    AgentInstance listener = speakerId == agentA.AgentId ? agentB : agentA;

                    // 2-tier 하이브리드 폴백 매칭 적용
                    GossipItem? originalGossip = null;

                    // Tier 1: LLM이 식별하여 전달한 gossip_id를 기반으로 매칭 시도
                    if (!string.IsNullOrWhiteSpace(gossipElem.GossipId))
                    {
                        if (speaker.KnownGossips.TryGetValue(gossipElem.GossipId, out var knownGossip))
                        {
                            originalGossip = knownGossip.Gossip;
                        }
                    }

                    // Tier 2: LLM의 ID 매칭 실패 시, 이번 대화 시작 시 공유하기로 지정되었던 소문과 비교
                    if (originalGossip == null)
                    {
                        var targetGossipToShare = speaker.AgentId == agentA.AgentId ? gossipToShareByA : gossipToShareByB;
                        if (targetGossipToShare != null && targetGossipToShare.Gossip.Subject == subject)
                        {
                            originalGossip = targetGossipToShare.Gossip;
                        }
                    }

                    // Tier 3 (신규): 임베딩 유사도 기반 매칭 (ID가 다르게 생성되거나 누락되었을 때, 유사성 평가로 매칭)
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
                            Console.WriteLine($"[DialogueOrchestrator] 🧬 [임베딩 매칭 성공] 추출된 소문과 화자의 기존 소문 유사도 {maxSim:F3} 매칭 성공 (ID: {originalGossip.GossipId})");
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
                            SpeakerId = speaker.AgentId,
                            ListenerId = listener.AgentId,
                            SubjectId = originalGossip.Subject,
                            GossipContent = content,
                            IsMutated = isMutated
                        }
                    };
                    await _broadcaster.BroadcastAsync(gossipEvent);

                    // 소문 전파를 중기 에피소드 기억으로 귀속
                    string subjectName = (subject == agentA.AgentId) ? agentA.Persona.Name : ((subject == agentB.AgentId) ? agentB.Persona.Name : subject);
                    listener.MemoryBox.AddEpisode(new Episode
                    {
                        Timestamp = DateTime.Now,
                        TargetName = speaker.Persona.Name,
                        Summary = $"{speaker.Persona.Name}(으)로부터 {subjectName}에 대한 소문(\"{content}\")을 들었습니다.",
                        InvolvedAgentIds = new List<string> { speaker.AgentId, listener.AgentId, subject }
                    });

                    // 소문 유통 메모리 로깅
                    string gossipLog = $"소문 전파 ({speaker.Persona.Name} ➔ {listener.Persona.Name}): 대상={originalGossip.Subject}, 내용=\"{content}\"";
                    await _memoryLogger.LogMemoryEventAsync(gossipLog);
                }
            }

            Console.WriteLine($"📝 기록된 에피소드 요약: \"{summary}\"");

            var lines = conversationHistory.Select(m => {
                string name = m.Role == agentA.AgentId ? agentA.Persona.Name : agentB.Persona.Name;
                return $"{name}: {m.Text}";
            }).ToList();

            var structuredLines = conversationHistory.Select(m => {
                string name = m.Role == agentA.AgentId ? agentA.Persona.Name : agentB.Persona.Name;
                return new MundusVivens.Prototype.Protos.DialogueLine
                {
                    SpeakerId = m.Role,
                    SpeakerName = name,
                    Text = m.Text
                };
            }).ToList();

             Console.WriteLine($"=======================================================\n");
             return new DialogueResult 
             { 
                 Summary = summary, 
                 DialogueLines = lines, 
                 StructuredLines = structuredLines,
                 EmotionUpdates = emotionUpdatesList
             };
         }
         else
         {
             Console.WriteLine($"[Error] 사후 데이터 분석 파싱 실패 (LlmJsonParser 반환값이 null입니다).");
             Console.WriteLine($"Raw Response: {postResponse}");
         }
         Console.WriteLine($"=======================================================\n");
 
         var fallbackStructuredLines = conversationHistory.Select(m => {
             string name = m.Role == agentA.AgentId ? agentA.Persona.Name : agentB.Persona.Name;
             return new MundusVivens.Prototype.Protos.DialogueLine
             {
                 SpeakerId = m.Role,
                 SpeakerName = name,
                 Text = m.Text
             };
         }).ToList();
 
         return new DialogueResult
         {
             Summary = "대화 분석에 실패했습니다.",
             DialogueLines = conversationHistory.Select(m => {
                 string name = m.Role == agentA.AgentId ? agentA.Persona.Name : agentB.Persona.Name;
                 return $"{name}: {m.Text}";
             }).ToList(),
             StructuredLines = fallbackStructuredLines,
             EmotionUpdates = emotionUpdatesList
         };
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
    /// </summary>
    private List<KnownGossip> ComputeTopKGossips(AgentInstance speaker, AgentInstance listener, int k = 3)
    {
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

    private string CleanResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        return response.Trim().Replace("\"", "");
    }
}
