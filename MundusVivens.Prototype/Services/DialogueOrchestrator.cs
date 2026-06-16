using MundusVivens.Prototype.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IDialogueOrchestrator
{
    Task RunConversationAsync(AgentInstance agentA, AgentInstance agentB, CancellationToken cancellationToken = default);
}

public class DialogueOrchestrator : IDialogueOrchestrator
{
    private readonly IGeminiApiService _apiService;
    private readonly IGossipEngine _gossipEngine;

    public DialogueOrchestrator(IGeminiApiService apiService, IGossipEngine gossipEngine)
    {
        _apiService = apiService;
        _gossipEngine = gossipEngine;
    }

    public async Task RunConversationAsync(AgentInstance agentA, AgentInstance agentB, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"\n=======================================================");
        Console.WriteLine($"💬 대화 시작: {agentA.Persona.Name} ({agentA.Persona.Job})  ◀ ▷  {agentB.Persona.Name} ({agentB.Persona.Job})");
        Console.WriteLine($"📍 위치: {agentA.Status.CurrentLocation}");
        
        var relAToB = GetOrCreateRelationship(agentA, agentB.AgentId);
        var relBToA = GetOrCreateRelationship(agentB, agentA.AgentId);
        Console.WriteLine($"   * {agentA.Persona.Name}의 태도: 호감도 {relAToB.Liking}, 신뢰도 {relAToB.Trust}");
        Console.WriteLine($"   * {agentB.Persona.Name}의 태도: 호감도 {relBToA.Liking}, 신뢰도 {relBToA.Trust}");
        Console.WriteLine($"=======================================================\n");

        var gossipToShareByA = _gossipEngine.SelectGossipToShare(agentA, agentB);
        var gossipToShareByB = _gossipEngine.SelectGossipToShare(agentB, agentA);

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
                .Where(e => e.TargetName == currentListener.Persona.Name || e.TargetName == currentListener.AgentId)
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

            string systemPrompt = $@"당신은 가상 세계 시뮬레이션의 NPC [{currentSpeaker.Persona.Name}]입니다. 주어진 페르소나와 상대방에 대한 기억을 바탕으로 대답하십시오.

[내 페르소나]
- 이름/직업: {currentSpeaker.Persona.Name} / {currentSpeaker.Persona.Job}
- 성격/말투: {currentSpeaker.Persona.ToneStyle}
- 배경 이야기: {currentSpeaker.Persona.Backstory}
- 핵심 가치관: {currentSpeaker.Persona.CoreValues}

[대화 상대방 정보]
- 이름/직업: {currentListener.Persona.Name} / {currentListener.Persona.Job}
- 상대에 대한 나의 태도: 호감도 {rel.Liking}/100, 신뢰도 {rel.Trust}/100

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
                GenerationConfig: new GenerationConfig(0.7f, 2048, "text/plain")
            );

            string dialogueResponse = await _apiService.SendMessageAsync(request, ModelTier.Flash35, cancellationToken);
            dialogueResponse = CleanResponse(dialogueResponse);

            Console.WriteLine($"💬 \x1b[36m{currentSpeaker.Persona.Name}\x1b[0m: {dialogueResponse}");
            
            var chatMessage = new ChatMessage(currentSpeaker.AgentId, dialogueResponse);
            conversationHistory.Add(chatMessage);

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

        string postProcessSystemPrompt = $@"당신은 두 NPC 간의 대화 내용을 분석하고 관계 변화 및 전파된 소문을 기록하는 월드 관리 시스템입니다.
다음 대화를 객관적으로 분석하여 지정된 JSON 구조로만 출력하십시오. 절대 ```json 과 같은 마크다운 코드 블록이나 추가 문장을 붙이지 말고 순수 JSON만 반환하십시오.

[대화 참여자]
- 에이전트 A: {agentA.Persona.Name} (ID: {agentA.AgentId})
- 에이전트 B: {agentB.Persona.Name} (ID: {agentB.AgentId})

[대화 원본]
<chat_log>
{chatLog}
</chat_log>

[분석 규칙]
1. summary: 대화 요약을 3인칭 소설 기술처럼 작성하되, 수치(골드, 수치 스탯 등)는 배제하고 1문장으로 요약하십시오.
2. relationship_changes: 대화 내용을 바탕으로 서로에 대한 호감도(liking)와 신뢰도(trust) 변화량을 -10에서 +10 사이 정수값(delta)으로 산출하십시오. 친화적이면 +, 다툼/불신이 커지면 -입니다.
3. gossips_exchanged: 대화 중 소문이나 특정 정보가 전파되었는지 분석하십시오.
   - subject: 소문의 대상이 된 인물의 AgentId (예: 'npc_kyle' 또는 'npc_bart' 또는 'npc_eva'). 대화에서 해당 대상이 직접 지목된 경우에만 추출하십시오.
   - content: 대화 중 발설된 소문의 핵심 요약 내용 (예: '성물을 훔쳤다')
   - credibility_rating: 들려온 이야기에 대해 화자가 보인 신빙성 정도 (0 ~ 100)
   - speaker_id: 소문을 말한 화자의 AgentId

[출력 포맷]
{{
  ""summary"": ""에이전트 A와 B가 안부를 주고받으며 일상 대화를 나눴습니다."",
  ""relationship_changes"": {{
    ""liking_delta_a_to_b"": 0,
    ""trust_delta_a_to_b"": 0,
    ""liking_delta_b_to_a"": 0,
    ""trust_delta_b_to_a"": 0
  }},
  ""gossips_exchanged"": []
}}
";

        var postRequest = new GeminiRequest(
            SystemInstruction: new Content("system", new List<Part> { new Part(postProcessSystemPrompt) }),
            Contents: new List<Content> { new Content("user", new List<Part> { new Part("분석 시작.") }) },
            GenerationConfig: new GenerationConfig(0.1f, 400, "application/json")
        );

        string postResponse = await _apiService.SendMessageAsync(postRequest, ModelTier.FlashLite, cancellationToken);
        postResponse = StripMarkdown(postResponse);

        try
        {
            using var doc = JsonDocument.Parse(postResponse);
            var root = doc.RootElement;

            string summary = root.GetProperty("summary").GetString() ?? "대화 완료";
            var timestamp = DateTime.Now;

            agentA.MemoryBox.AddEpisode(new Episode { Timestamp = timestamp, TargetName = agentB.Persona.Name, Summary = summary });
            agentB.MemoryBox.AddEpisode(new Episode { Timestamp = timestamp, TargetName = agentA.Persona.Name, Summary = summary });

            var changes = root.GetProperty("relationship_changes");
            int likingDeltaAToB = changes.GetProperty("liking_delta_a_to_b").GetInt32();
            int trustDeltaAToB = changes.GetProperty("trust_delta_a_to_b").GetInt32();
            int likingDeltaBToA = changes.GetProperty("liking_delta_b_to_a").GetInt32();
            int trustDeltaBToA = changes.GetProperty("trust_delta_b_to_a").GetInt32();

            relAToB.Liking = Math.Clamp(relAToB.Liking + likingDeltaAToB, -100, 100);
            relAToB.Trust = Math.Clamp(relAToB.Trust + trustDeltaAToB, 0, 100);

            relBToA.Liking = Math.Clamp(relBToA.Liking + likingDeltaBToA, -100, 100);
            relBToA.Trust = Math.Clamp(relBToA.Trust + trustDeltaBToA, 0, 100);

            Console.WriteLine($"📈 관계 갱신 완료:");
            Console.WriteLine($"   * {agentA.Persona.Name} ➔ {agentB.Persona.Name}: 호감도 {relAToB.Liking} ({likingDeltaAToB:+#;-#;0}), 신뢰도 {relAToB.Trust} ({trustDeltaAToB:+#;-#;0})");
            Console.WriteLine($"   * {agentB.Persona.Name} ➔ {agentA.Persona.Name}: 호감도 {relBToA.Liking} ({likingDeltaBToA:+#;-#;0}), 신뢰도 {relBToA.Trust} ({trustDeltaBToA:+#;-#;0})");

            if (root.TryGetProperty("gossips_exchanged", out var gossipsArray) && gossipsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var gossipElem in gossipsArray.EnumerateArray())
                {
                    string subject = gossipElem.GetProperty("subject").GetString() ?? string.Empty;
                    string content = gossipElem.GetProperty("content").GetString() ?? string.Empty;
                    string speakerId = gossipElem.GetProperty("speaker_id").GetString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(content)) continue;

                    AgentInstance speaker = speakerId == agentA.AgentId ? agentA : agentB;
                    AgentInstance listener = speakerId == agentA.AgentId ? agentB : agentA;

                    // 이번 대화에서 발설 대상으로 지정된 소문이 있고 대상이 일치하면 우선 매치, 없으면 폴백 검색
                    GossipItem? originalGossip = null;
                    var selectedGossip = speaker.AgentId == agentA.AgentId ? gossipToShareByA : gossipToShareByB;
                    if (selectedGossip != null && selectedGossip.Gossip.Subject == subject)
                    {
                        originalGossip = selectedGossip.Gossip;
                    }
                    else
                    {
                        originalGossip = speaker.KnownGossips.Values.FirstOrDefault(kg => kg.Gossip.Subject == subject)?.Gossip;
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

                    _gossipEngine.ProcessGossipSharing(speaker, listener, originalGossip, content);
                }
            }

            Console.WriteLine($"📝 기록된 에피소드 요약: \"{summary}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] 사후 데이터 분석 파싱 실패: {ex.Message}");
            Console.WriteLine($"Raw Response: {postResponse}");
        }
        Console.WriteLine($"=======================================================\n");
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

    private string CleanResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        return response.Trim().Replace("\"", "");
    }

    private string StripMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string clean = text.Trim();
        
        int firstBrace = clean.IndexOf('{');
        int lastBrace = clean.LastIndexOf('}');
        if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
        {
            return clean.Substring(firstBrace, lastBrace - firstBrace + 1);
        }
        return clean;
    }
}
