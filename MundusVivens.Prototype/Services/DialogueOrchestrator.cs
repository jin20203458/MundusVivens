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
    public List<NextJobDto> NextJobs { get; set; } = new(); // 🆕 대화 종료 후 공동 계획 수립 결과
}

public interface IDialogueOrchestrator
{
    Task<DialogueResult> RunConversationAsync(AgentInstance agentA, AgentInstance agentB, ulong taskId = 0, CancellationToken cancellationToken = default);
    Task<DialogueResult> RunGroupConversationAsync(List<AgentInstance> participants, ulong taskId = 0, CancellationToken cancellationToken = default);
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

    public async Task<DialogueResult> RunConversationAsync(AgentInstance agentA, AgentInstance agentB, ulong taskId = 0, CancellationToken cancellationToken = default)
    {
        return await RunGroupConversationAsync(new List<AgentInstance> { agentA, agentB }, taskId, cancellationToken);
    }

    private readonly Random _random = new();

    private int ComputeDistortionLevel(AgentInstance speaker, AgentInstance listener, GossipItem gossip)
    {
        double distortionScore = 0.0;
        
        // 1. 외향성이 높을수록 과장 경향 (0.0 ~ 0.3)
        distortionScore += (speaker.Persona.Extroversion - 0.5) * 0.6;
        
        // 2. 소문 대상에 대한 적대감이 높으면 악의적 왜곡 (0.0 ~ 0.4)
        if (speaker.RelationshipMap.TryGetValue(gossip.Subject, out var subjectRel))
            distortionScore += Math.Max(0, -subjectRel.Liking / 250.0);
        
        // 3. 이미 많이 왜곡된 소문은 추가 왜곡 확률 증가 (0.0 ~ 0.2)
        distortionScore += Math.Min(gossip.MutationCount * 0.05, 0.2);
        
        // 4. 듣는 이와의 신뢰가 낮으면 정보를 축소 (0.0 ~ 0.1)
        if (speaker.RelationshipMap.TryGetValue(listener.AgentId, out var listenerRel))
            distortionScore += Math.Max(0, (50 - listenerRel.Trust) / 500.0);
        
        if (distortionScore >= 0.5) return 2; // 심한 왜곡
        if (distortionScore >= 0.2) return 1; // 약간 과장
        return 0; // 정확 전달
    }

    public async Task<DialogueResult> RunGroupConversationAsync(List<AgentInstance> participants, ulong taskId = 0, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"\n=======================================================");
        Console.WriteLine($"💬 다자간 대화 시작: {string.Join(" ◀ ▷ ", participants.Select(p => p.Persona.Name))}");
        Console.WriteLine($"📍 위치: {participants[0].Status.CurrentLocation}");
        Console.WriteLine($"=======================================================\n");

        // 1. Decay gossips for all participants
        foreach (var p in participants)
        {
            _gossipEngine.DecayAgentGossips(p, _gossipEngine.CurrentTick);
        }

        // 2. Select gossip to share targeting other participants
        var gossipToShareMap = new Dictionary<string, (KnownGossip Gossip, AgentInstance TargetListener)>();
        foreach (var participant in participants)
        {
            var otherParticipants = participants.Where(p => p.AgentId != participant.AgentId).ToList();
            if (otherParticipants.Count == 0) continue;
            var targetListener = otherParticipants[_random.Next(otherParticipants.Count)];
            
            var topK = ComputeTopKGossips(participant, targetListener);
            var selectedGossip = _gossipEngine.SelectGossipToShare(participant, targetListener, topK);
            if (selectedGossip != null)
            {
                gossipToShareMap[participant.AgentId] = (selectedGossip, targetListener);
            }
        }

        var gossipInstructions = new List<string>();
        foreach (var item in gossipToShareMap)
        {
            var speakerId = item.Key;
            var (gossip, targetListener) = item.Value;
            var speaker = participants.First(p => p.AgentId == speakerId);
            
            int distortionLevel = ComputeDistortionLevel(speaker, targetListener, gossip.Gossip);
            
            string levelDesc = distortionLevel switch
            {
                2 => "심한 왜곡 (당신의 편견과 감정을 반영하여 사건을 크게 과장하거나 중요한 사실을 마음대로 변형/조작하여 전달하십시오.)",
                1 => "약간 과장 (감정적인 조미료를 쳐서 살짝 부풀려 전달하십시오.)",
                _ => "정확 전달 (알고 있는 소문 내용을 있는 그대로 최대한 정확하게 사실적으로 전달하십시오.)"
            };
            
            gossipInstructions.Add($"- 화자: {speaker.Persona.Name}, 전달 대상: {targetListener.Persona.Name}\n" +
                                   $"  소물 대상: {gossip.Gossip.Subject}, 소문 내용: \"{gossip.Gossip.Content}\"\n" +
                                   $"  왜곡 가이드라인 [레벨 {distortionLevel}]: {levelDesc}\n" +
                                   $"  지시: 대화 중 기회가 된다면 상대방 실명을 언급하며 이 가이드라인에 맞추어 소문을 자연스럽게 흘리거나 폭로하십시오.");
        }
        string gossipInstructionsStr = gossipInstructions.Any() 
            ? string.Join("\n\n", gossipInstructions) 
            : "이번 대화에서 공유할 새로운 소문 지시가 없습니다. 평범한 대화를 진행하십시오.";

        // 3. Build participant profiles
        var participantDetails = new List<string>();
        foreach (var p in participants)
        {
            var relsStr = new List<string>();
            foreach (var other in participants.Where(o => o.AgentId != p.AgentId))
            {
                var rel = GetOrCreateRelationship(p, other.AgentId);
                relsStr.Add($"{other.Persona.Name}에 대한 태도(호감도: {rel.Liking}/100, 신뢰도: {rel.Trust}/100)");
            }
            
            var relevantEpisodes = p.MemoryBox.EpisodicMemories
                .Where(e => e.InvolvedAgentIds != null && e.InvolvedAgentIds.Intersect(participants.Select(part => part.AgentId)).Any())
                .Select(e => $"[{e.Timestamp:HH:mm}] {e.Summary}");
            
            string relevantMemoriesStr = string.Join("\n", relevantEpisodes);
            if (p.MemoryBox.CoreMemories.Any())
            {
                relevantMemoriesStr += "\n[장기 기억]\n" + string.Join("\n", p.MemoryBox.CoreMemories.Select(c => $"- {c.Content}"));
            }
            if (string.IsNullOrWhiteSpace(relevantMemoriesStr))
            {
                relevantMemoriesStr = "특별한 과거 기억이 없습니다.";
            }

            participantDetails.Add($@"### 에이전트 [{p.Persona.Name}] (ID: {p.AgentId})
- 이름/직업: {p.Persona.Name} / {p.Persona.Job}
- 성격/말투: {p.Persona.ToneStyle}
- 배경 이야기: {p.Persona.Backstory}
- 감정 상태: {p.Status.Emotion}
- 활동 상태: {p.Status.Activity}
- 위치: {p.Status.CurrentLocation}
- 대화 전 계획했던 행동: {(p.Status.HasActiveJob ? p.Status.ActiveJobIntent : "없음")} (목표 장소: {(p.Status.HasActiveJob ? p.Status.ActiveJobLocation : "없음")})
- 타 참가자들과의 관계:
  {string.Join("\n  ", relsStr)}
- 관련 기억 맥락:
{relevantMemoriesStr}");
        }

        string systemPrompt = $$"""
<role>월드 오케스트레이터</role>
<task>참여자 정보와 소문 폭로 지시를 바탕으로 다자간 대화 대본을 작성하고, 그 대화의 결과로 생겨난 관계 변화, 감정 변화, 유통된 소문 정보, 그리고 대화 직후 참여자들의 다음 행동 계획(next_jobs)을 일괄 생성하십시오.</task>

<participants>
[참여자 목록 및 정보]
{{string.Join("\n\n", participantDetails)}}
</participants>

<gossip_instructions>
[소문 폭로 지시 및 왜곡 가이드라인]
{{gossipInstructionsStr}}
</gossip_instructions>

<rules>
[대화 및 분석 규칙]
1. lines (대본):
   - 참여자들이 나누는 대화 대본을 순차적으로 작성하십시오.
   - 대사 수: 총 {{participants.Count * 2}}줄 내외로 작성하십시오. (각 참여자가 골고루 1~2회 이상 발언하도록 유도)
   - 대본에는 대사 텍스트만 포함하고, 행동 묘사나 지문 등의 메타 설명은 완전히 배제하십시오.
   - 각 참여자의 성격/말투/태도를 적극 연기하십시오.
2. summary (대화 요약):
   - 대화 내용을 바탕으로 3인칭 소설 기술처럼 작성하되 수치는 배제하고 1문장으로 요약하십시오.
3. relationship_changes (관계 변화):
   - 대화 내용을 바탕으로 각 화자 간에 발생한 호감도(liking)와 신뢰도(trust) 변화량을 -10에서 +10 사이 정수값(delta)으로 산출하십시오.
   - from과 to에 해당하는 AgentId를 명시하십시오. (예: from: "npc_eva", to: "npc_bart")
4. emotion_updates (대화 후 감정 변화):
   - 대화 내용을 반영하여 대화 종료 시점의 최종 감정 단어 하나(예: '평온함', '유쾌함', '분노', '의심' 등)를 기록하십시오.
5. gossips_exchanged (유통된 소문 분석):
   - 대화 중 소문이 실제로 폭로되거나 전파되었는지 분석하여 기록하십시오.
   - gossip_id: 주어진 [소문 폭로 지시]에 매칭되는 소문일 경우 해당 ID를 정확히 쓰십시오.
   - subject: 소문의 대상 AgentId
   - content: 실제로 대화에서 흘려진 소문 텍스트 (왜곡 가이드라인에 의해 왜곡/변형된 형태 그대로 추출되어야 함)
   - credibility_rating: 들려온 이야기에 대해 화자가 보인 신빙성 정도 (0 ~ 100)
   - speaker_id: 소문을 말한 화자의 AgentId
6. next_jobs (대화 후 다음 행동 계획):
   - 대화 종료 후 각 참여자가 이동할 다음 장소(target_location)와 수행할 구체적인 행동(activity)을 결정하십시오.
   - 대화 내용에서 동행하거나 새로운 행동을 같이 하기로 약속하지 않았다면, 무리하게 이동할 필요 없이 각자 [대화 전 계획했던 행동]의 목표 장소와 행동을 그대로 다시 수행하도록 결정해야 합니다. (원래의 계획이 '없음'이었다면 현재 위치에서 대기하거나 일상 활동을 계속하도록 하십시오.)
   - 대화 내용에서 두 에이전트가 무언가 함께하기로 합의했다면(예: 술을 마시러 술집으로 가자고 함), target_location을 반드시 동일하게 일치시키십시오.
   - target_location은 반드시 아래 [이동 가능한 장소 목록] 중 하나여야 합니다.
   - activity는 그 장소에서 할 구체적인 행동(예: "대장간에서 철광석 제련 작업을 이어서 한다" 또는 "술집으로 이동해 함께 술을 마신다")이어야 합니다.

[이동 가능한 장소 목록]
- 영주 저택 (Manor)
- 성당 (Church)
- 경비 초소 (Guard Post)
- 연금술 공방 (Alchemy Lab)
- 마을 광장 (Square)
- 대장간 (Forge)
- 뒷골목 (Back Alley)
- 술집 (Tavern)
</rules>

<output_format>
[출력 포맷 (반드시 아래 JSON 스키마를 철저히 지키십시오)]
{
  "lines": [
    { "speaker_id": "npc_eva", "text": "대사 내용 1" },
    { "speaker_id": "npc_bart", "text": "대사 내용 2" }
  ],
  "summary": "요약 문장",
  "relationship_changes": [
    { "from": "npc_eva", "to": "npc_bart", "liking_delta": 2, "trust_delta": 1 },
    { "from": "npc_bart", "to": "npc_eva", "liking_delta": 0, "trust_delta": 0 }
  ],
  "emotion_updates": [
    { "agent_id": "npc_eva", "new_emotion": "의심" },
    { "agent_id": "npc_bart", "new_emotion": "평온함" }
  ],
  "gossips_exchanged": [
    { "gossip_id": "gossip_...", "subject": "npc_kyle", "content": "왜곡되어 말해진 소문 내용", "credibility_rating": 80, "speaker_id": "npc_eva" }
  ],
  "next_jobs": [
    { "agent_id": "npc_eva", "target_location": "술집 (Tavern)", "activity": "Bart와 술집에서 술을 마시며 이야기를 나눈다" },
    { "agent_id": "npc_bart", "target_location": "술집 (Tavern)", "activity": "Eva와 술집에서 술을 마시며 이야기를 나눈다" }
  ]
}
</output_format>
""";

        var postRequest = new GeminiRequest(
            SystemInstruction: new Content("system", new List<Part> { new Part(systemPrompt) }),
            Contents: new List<Content> { new Content("user", new List<Part> { new Part("대본 작성을 시작하십시오.") }) },
            GenerationConfig: new GenerationConfig(null, 4000, "application/json", null, new ThinkingConfig(ThinkingLevel.low))
        );

        string postResponse = await _apiService.SendMessageAsync(postRequest, ModelTier.Flash35, cancellationToken);
        var analysis = LlmJsonParser.DeserializeSafe<GroupConversationScript>(postResponse);

        if (analysis == null)
        {
            throw new Exception("대화 생성 JSON 파싱 실패");
        }

        // 4. Live broadcast lines one by one to C++ and Unity
        var lines = new List<string>();
        var structuredLines = new List<DialogueLine>();

        foreach (var lineInfo in analysis.Lines)
        {
            var speaker = participants.FirstOrDefault(p => p.AgentId == lineInfo.SpeakerId);
            if (speaker == null) continue;

            string lineText = CleanResponse(lineInfo.Text);
            Console.WriteLine($"💬 \x1b[36m{speaker.Persona.Name}\x1b[0m: {lineText}");

            lines.Add($"{speaker.Persona.Name}: {lineText}");

            var sLine = new DialogueLine
            {
                SpeakerId = speaker.NumericId,
                SpeakerName = speaker.Persona.Name,
                Text = lineText
            };
            structuredLines.Add(sLine);

            if (taskId != 0)
            {
                var liveEvent = new WorldEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Dialogue = new DialogueEvent
                    {
                        TaskId = taskId,
                        AgentAId = participants[0].NumericId,
                        AgentBId = participants.Count > 1 ? participants[1].NumericId : 0,
                        Location = participants[0].Status.CurrentLocation,
                        IsStarted = true
                    }
                };
                liveEvent.Dialogue.ParticipantIds.AddRange(participants.Select(p => p.NumericId));
                liveEvent.Dialogue.Lines.Add(sLine);
                await _broadcaster.BroadcastAsync(liveEvent);
            }

            // Artificially delay a tiny bit so clients can render speech bubbles sequentially
            await Task.Delay(1000, cancellationToken);
        }

        // 5. Apply updates
        var emotionUpdatesList = new List<AgentEmotionUpdate>();
        if (analysis.EmotionUpdates != null)
        {
            foreach (var emUpdate in analysis.EmotionUpdates)
            {
                var agent = participants.FirstOrDefault(p => p.AgentId == emUpdate.AgentId);
                if (agent == null) continue;

                agent.Status.Emotion = emUpdate.NewEmotion;
                Console.WriteLine($"🎭 감정 업데이트: {agent.Persona.Name} ➔ {emUpdate.NewEmotion}");

                emotionUpdatesList.Add(new AgentEmotionUpdate
                {
                    AgentId = agent.NumericId,
                    NewEmotion = emUpdate.NewEmotion
                });
            }
        }

        // Save episodes
        var timestamp = DateTime.Now;
        foreach (var agent in participants)
        {
            agent.MemoryBox.AddEpisode(new Episode
            {
                Timestamp = timestamp,
                TargetName = string.Join(", ", participants.Where(p => p.AgentId != agent.AgentId).Select(p => p.Persona.Name)),
                Summary = analysis.Summary,
                InvolvedAgentIds = participants.Select(p => p.AgentId).ToList()
            });
        }

        // Apply relationship changes
        if (analysis.RelationshipChanges != null)
        {
            foreach (var rc in analysis.RelationshipChanges)
            {
                var fromAgent = participants.FirstOrDefault(p => p.AgentId == rc.From);
                var toAgent = participants.FirstOrDefault(p => p.AgentId == rc.To);
                if (fromAgent == null || toAgent == null) continue;

                var rel = GetOrCreateRelationship(fromAgent, toAgent.AgentId);
                rel.Liking = Math.Clamp(rel.Liking + rc.LikingDelta, -100, 100);
                rel.Trust = Math.Clamp(rel.Trust + rc.TrustDelta, 0, 100);
                RelationshipChangeTracker.TrackChange(fromAgent.NumericId, toAgent.NumericId, rel.Liking, rel.Trust);

                Console.WriteLine($"   * {fromAgent.Persona.Name} ➔ {toAgent.Persona.Name}: 호감도 {rel.Liking} ({rc.LikingDelta:+#;-#;0}), 신뢰도 {rel.Trust} ({rc.TrustDelta:+#;-#;0})");

                // Broadcast relationship change
                var relEvent = new WorldEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Relationship = new RelationshipEvent
                    {
                        FromAgentId = fromAgent.NumericId,
                        ToAgentId = toAgent.NumericId,
                        NewAffinity = rel.Liking,
                        AffinityDelta = rc.LikingDelta,
                        NewTrust = rel.Trust,
                        TrustDelta = rc.TrustDelta
                    }
                };
                await _broadcaster.BroadcastAsync(relEvent);
            }
        }

        // Apply gossip sharing
        if (analysis.GossipsExchanged != null)
        {
            foreach (var gossipElem in analysis.GossipsExchanged)
            {
                string subject = gossipElem.Subject;
                string content = gossipElem.Content;
                string speakerId = gossipElem.SpeakerId;

                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(content)) continue;

                var speaker = participants.FirstOrDefault(p => p.AgentId == speakerId);
                if (speaker == null) continue;

                var listeners = participants.Where(p => p.AgentId != speakerId).ToList();

                foreach (var listener in listeners)
                {
                    // Find original gossip
                    GossipItem? originalGossip = null;
                    if (!string.IsNullOrWhiteSpace(gossipElem.GossipId))
                    {
                        if (speaker.KnownGossips.TryGetValue(gossipElem.GossipId, out var knownGossip))
                        {
                            originalGossip = knownGossip.Gossip;
                        }
                    }

                    if (originalGossip == null)
                    {
                        if (gossipToShareMap.TryGetValue(speakerId, out var tuple) && tuple.Gossip.Gossip.Subject == subject)
                        {
                            originalGossip = tuple.Gossip.Gossip;
                        }
                    }

                    if (originalGossip == null)
                    {
                        var candidates = speaker.KnownGossips.Values.Where(kg => kg.Gossip.Subject == subject).ToList();
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
                        }
                    }

                    if (originalGossip == null)
                    {
                        originalGossip = new GossipItem
                        {
                            GossipId = $"gossip_{subject}_{Guid.NewGuid().ToString().Substring(0, 5)}",
                            Subject = subject,
                            Content = content,
                            SourceAgentId = speakerId,
                            BaseCredibility = 70
                        };
                        
                        speaker.KnownGossips[originalGossip.GossipId] = new KnownGossip
                        {
                            Gossip = originalGossip,
                            SubjectiveBelief = 1.0,
                            HasSharedWithOthers = true,
                            LastDecayedAtTick = _gossipEngine.CurrentTick
                        };
                    }

                    await _gossipEngine.ProcessGossipSharingAsync(speaker, listener, originalGossip, content);

                    // Broadcast gossip sharing event
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

                    // 소문 전파를 중기 에피소드 기억으로 귀속
                    string subjectName = (subject == speaker.AgentId) ? speaker.Persona.Name : subject;
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
        }

        // Apply next jobs
        if (analysis.NextJobs != null)
        {
            foreach (var nj in analysis.NextJobs)
            {
                var agent = participants.FirstOrDefault(p => p.AgentId == nj.AgentId);
                if (agent == null) continue;

                ulong newJobId = MundusVivensGrpcService.GenerateNextJobId();
                string correctedLocation = MapToValidLocation(nj.TargetLocation);

                agent.Status.ActiveJobId = newJobId;
                agent.Status.ActiveJobLocation = correctedLocation;
                agent.Status.ActiveJobIntent = nj.Activity;

                Console.WriteLine($"🧠 [Post-Dialogue Plan] NPC '{agent.Persona.Name}'의 새로운 행동 결정: 위치={correctedLocation}, 행동={nj.Activity}");
            }
        }

        Console.WriteLine($"📝 기록된 에피소드 요약: \"{analysis.Summary}\"");
        Console.WriteLine($"=======================================================\n");

        return new DialogueResult
        {
            Summary = analysis.Summary,
            DialogueLines = lines,
            StructuredLines = structuredLines,
            EmotionUpdates = emotionUpdatesList,
            NextJobs = analysis.NextJobs ?? new()
        };
    }

    private string MapToValidLocation(string rawLocation)
    {
        if (string.IsNullOrWhiteSpace(rawLocation)) return "마을 광장 (Square)";
        var lower = rawLocation.ToLower();
        if (lower.Contains("저택") || lower.Contains("manor")) return "영주 저택 (Manor)";
        if (lower.Contains("성당") || lower.Contains("church")) return "성당 (Church)";
        if (lower.Contains("초소") || lower.Contains("경비") || lower.Contains("guard")) return "경비 초소 (Guard Post)";
        if (lower.Contains("연금") || lower.Contains("공방") || lower.Contains("alchemy") || lower.Contains("lab")) return "연금술 공방 (Alchemy Lab)";
        if (lower.Contains("대장간") || lower.Contains("forge")) return "대장간 (Forge)";
        if (lower.Contains("골목") || lower.Contains("alley")) return "뒷골목 (Back Alley)";
        if (lower.Contains("술집") || lower.Contains("tavern")) return "술집 (Tavern)";
        return "마을 광장 (Square)";
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

    private string CleanResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        return response.Trim().Replace("\"", "");
    }
}
