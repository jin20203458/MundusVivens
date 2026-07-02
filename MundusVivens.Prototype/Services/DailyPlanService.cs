using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Protos;
using MundusVivens.Prototype.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IDailyPlanService
{
    Task PerformReflectionAndGenerateSchedulesAsync(int tickNumber, CancellationToken cancellationToken = default);
    GetDailySchedulesResponse GetSchedulesForTick(int currentTick);
    void InitializeDefaultSchedules();
}

public class DailyPlanService : IDailyPlanService
{
    private readonly IGeminiApiService _apiService;
    private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
    private readonly ConcurrentDictionary<string, List<DailyScheduleItem>> _cachedSchedules = new();

    public DailyPlanService(
        IGeminiApiService apiService,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor)
    {
        _apiService = apiService;
        _agentsAccessor = agentsAccessor;
    }

    public void InitializeDefaultSchedules()
    {
        var agents = _agentsAccessor();
        foreach (var agentId in agents.Keys)
        {
            if (!_cachedSchedules.ContainsKey(agentId))
            {
                _cachedSchedules[agentId] = GetFallbackSchedule(agentId);
            }
        }
    }

    public GetDailySchedulesResponse GetSchedulesForTick(int currentTick)
    {
        var response = new GetDailySchedulesResponse();
        var agents = _agentsAccessor();

        // 만약 캐시된 일정이 하나도 없다면 기본 일정으로 강제 초기화
        if (_cachedSchedules.IsEmpty)
        {
            InitializeDefaultSchedules();
        }

        foreach (var kvp in _cachedSchedules)
        {
            if (kvp.Key == "player") continue; // 플레이어는 계획 수립에서 제외

            var dailySchedule = new DailySchedule
            {
                AgentId = AgentIdMapping.GetNumericId(kvp.Key)
            };
            dailySchedule.Items.AddRange(kvp.Value);
            response.Schedules.Add(dailySchedule);
        }

        return response;
    }

    public async Task PerformReflectionAndGenerateSchedulesAsync(int tickNumber, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"\n[Reflection] 자정 직전(23틱) 감지: 에이전트 자아성찰 및 내일 일과 계획 수립 백그라운드 시작 (현재 틱: {tickNumber})");
        
        var agents = _agentsAccessor();
        var tasks = new List<Task>();

        foreach (var agent in agents.Values)
        {
            if (agent.AgentId == "player") continue;

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // 1. 자아성찰 (Reflection) 수행
                    await ReflectOnEpisodesAsync(agent, cancellationToken);

                    // 2. 내일의 Daily Plan 생성 및 캐싱
                    var schedule = await GenerateDailyPlanAsync(agent, cancellationToken);
                    _cachedSchedules[agent.AgentId] = schedule;
                    
                    Console.WriteLine($"[Reflection] 에이전트 '{agent.Persona.Name}' 성찰 및 일정 수립 완료.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Reflection Error] 에이전트 '{agent.Persona.Name}' 성찰 실패: {ex.Message}");
                    // 실패 시 기존 일정 유지 혹은 기본 일정 폴백 적용
                    if (!_cachedSchedules.ContainsKey(agent.AgentId))
                    {
                        _cachedSchedules[agent.AgentId] = GetFallbackSchedule(agent.AgentId);
                    }
                }
            }, cancellationToken));
        }

        // 전체 비동기 태스크 병렬 수행 완료 대기
        await Task.WhenAll(tasks);
        Console.WriteLine("[Reflection] 모든 NPC의 자아성찰 및 스케줄 수립 완료.\n");
    }

    private async Task ReflectOnEpisodesAsync(AgentInstance agent, CancellationToken cancellationToken)
    {
        var episodes = agent.MemoryBox.EpisodicMemories.ToList();
        if (episodes.Count == 0)
        {
            Console.WriteLine($"[Reflection] 에이전트 '{agent.Persona.Name}'는 오늘 특별한 사건(대화)이 없어 일상 요약 성찰을 진행합니다.");
            return;
        }

        string episodesList = string.Join("\n", episodes.Select(e => $"- [{e.Timestamp:HH:mm}] {e.TargetName}과의 대화 요약: {e.Summary}"));

        string prompt = $$"""
<role>가상 세계 NPC [{{agent.Persona.Name}}]의 자아성찰(Reflection) 시스템</role>
<task>오늘 하루 동안 겪은 에피소드들을 분석하여, 캐릭터의 장기 기억(CoreFact)을 추출하십시오.</task>

<rules>
1. 오늘 겪은 단기 에피소드들을 종합하여 NPC가 깊이 깨닫거나 가치관에 반영할 장기 기억을 1~2개 도출하십시오.
2. 각 장기 기억은 NPC의 페르소나, 핵심 가치관, Faction(진영) 및 에피소드를 종합적으로 분석하여 작성되어야 합니다.
3. 다른 마크다운이나 텍스트를 절대 포함하지 말고 오직 JSON만 출력하십시오.
</rules>

<context>
[NPC 페르소나]
- 이름/직업: {{agent.Persona.Name}} / {{agent.Persona.Job}}
- Faction: {{agent.Persona.Faction}}
- 성격/말투: {{agent.Persona.ToneStyle}}
- 배경 이야기: {{agent.Persona.Backstory}}
- 핵심 가치관: {{agent.Persona.CoreValues}}

[오늘 하루 동안 겪은 에피소드]
{{episodesList}}
</context>

<output_format>
반드시 아래 JSON 스키마를 충실히 준수하는 순수 JSON만 반환하십시오.
{
  "core_facts": [
    {
      "content": "장기 기억 내용 (예: Eva가 나에게 거짓말을 했다는 사실을 알았고, 그녀를 더 이상 신뢰할 수 없다)",
      "importance": 8
    }
  ]
}
</output_format>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", new List<Part> { new Part(prompt) }),
            Contents: new List<Content> { new Content("user", new List<Part> { new Part("성찰 시작.") }) },
            GenerationConfig: new GenerationConfig(null, 4000, "application/json", null, new ThinkingConfig(ThinkingLevel.minimal))
        );

        string responseJson = await _apiService.SendMessageAsync(request, ModelTier.FlashLite, cancellationToken);
        var reflectionResult = LlmJsonParser.DeserializeSafe<ReflectionResponse>(responseJson);

        if (reflectionResult?.CoreFacts != null)
        {
            foreach (var fact in reflectionResult.CoreFacts)
            {
                if (string.IsNullOrWhiteSpace(fact.Content)) continue;

                // 기존 장기 기억 용량 제한 관리
                if (agent.MemoryBox.CoreMemories.Count >= MemoryBox.MaxCoreMemories)
                {
                    // 중요도가 가장 낮은 것 제거
                    var minFact = agent.MemoryBox.CoreMemories.OrderBy(f => f.Importance).FirstOrDefault();
                    if (minFact != null)
                    {
                        agent.MemoryBox.CoreMemories.Remove(minFact);
                    }
                }

                agent.MemoryBox.CoreMemories.Add(new CoreFact(fact.Content, fact.Importance));
                Console.WriteLine($"🧠 [Memory Reflection] {agent.Persona.Name}에게 새로운 장기 기억 추가: \"{fact.Content}\" (중요도: {fact.Importance})");
            }

            // 성찰에 성공했으므로 단기 에피소드 메모리 정리 (토큰 절약 및 망각 기획 반영)
            // 최근 대화의 연속성을 위해 완전히 지우지 않고 큐의 절반만 비우거나, 15개 초과분을 비움
            while (agent.MemoryBox.EpisodicMemories.Count > 3)
            {
                agent.MemoryBox.EpisodicMemories.TryDequeue(out _);
            }
        }
    }

    private async Task<List<DailyScheduleItem>> GenerateDailyPlanAsync(AgentInstance agent, CancellationToken cancellationToken)
    {
        string coreMemoriesList = agent.MemoryBox.CoreMemories.Any()
            ? string.Join("\n", agent.MemoryBox.CoreMemories.Select(cf => $"- {cf.Content} (중요도: {cf.Importance})"))
            : "마음에 간직하고 있는 특별한 장기 기억이 없습니다.";

        string prompt = $$"""
<role>NPC [{{agent.Persona.Name}}]의 하루 계획(Daily Schedule) 수립 시스템</role>
<task>NPC의 페르소나, 현재 상태, 관계도, 장기 기억을 고려하여 내일 하루(0시~23시) 동안의 구체적인 일정을 짜주십시오.</task>

<rules>
1. 0시부터 23시까지의 일정이 빈 틈 없이 연속적이어야 합니다. (예: 0~7, 7~10, 10~15, 15~18, 18~22, 22~23)
2. NPC의 직업과 Faction(진영), 대화 상대들과의 관계를 일정에 자연스럽게 녹여내십시오. 
3. 목표 장소(target_location)는 반드시 [이동 가능한 장소 목록] 중 하나여야 합니다. (정확히 일치 필수)
4. 24시간 계획을 3~6개의 시간대로 나누어 짜주십시오. 시작 시간과 종료 시간은 반드시 정수(0~23)여야 합니다.
5. 각 일정은 시작 시간, 종료 시간, 목표 장소, 해당 장소에서 할 구체적인 행동(activity)을 포함해야 합니다.
6. 다른 마크다운이나 텍스트를 포함하지 마십시오.
</rules>

<context>
[이동 가능한 장소 목록]
- 영주 저택 (Manor)
- 성당 (Church)
- 경비 초소 (Guard Post)
- 연금술 공방 (Alchemy Lab)
- 마을 광장 (Square)
- 대장간 (Forge)
- 뒷골목 (Back Alley)
- 술집 (Tavern)

[NPC 페르소나]
- 이름/직업: {{agent.Persona.Name}} / {{agent.Persona.Job}}
- Faction: {{agent.Persona.Faction}}
- 성격: {{agent.Persona.ToneStyle}}
- 배경 이야기: {{agent.Persona.Backstory}}
- 핵심 가치관: {{agent.Persona.CoreValues}}

[현재 감정 및 상태]
- 감정: {{agent.Status.Emotion}}
- 현재 위치: {{agent.Status.CurrentLocation}}

[기억하는 장기 기억들]
{{coreMemoriesList}}
</context>

<output_format>
반드시 아래 JSON 형식으로만 대답하십시오.
{
  "schedules": [
    {
      "start_hour": 0,
      "end_hour": 7,
      "target_location": "성당 (Church)",
      "activity": "취침 및 휴식"
    }
  ]
}
</output_format>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", new List<Part> { new Part(prompt) }),
            Contents: new List<Content> { new Content("user", new List<Part> { new Part("하루 일정 계획 생성 시작.") }) },
            GenerationConfig: new GenerationConfig(null, 8192, "application/json", null, new ThinkingConfig(ThinkingLevel.low))
        );

        string responseJson = await _apiService.SendMessageAsync(request, ModelTier.Flash35, cancellationToken);
        var scheduleResult = LlmJsonParser.DeserializeSafe<ScheduleResponse>(responseJson);

        if (scheduleResult?.Schedules != null && scheduleResult.Schedules.Count > 0)
        {
            var list = new List<DailyScheduleItem>();
            foreach (var item in scheduleResult.Schedules)
            {
                // 위치 보정
                string correctedLocation = MapToValidLocation(item.TargetLocation);
                
                list.Add(new DailyScheduleItem
                {
                    StartHour = item.StartHour,
                    EndHour = item.EndHour,
                    TargetLocation = correctedLocation,
                    Activity = item.Activity
                });
            }
            return list;
        }

        throw new Exception("LLM 일정 파싱 오류 또는 결과가 비어 있습니다.");
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
        
        return "마을 광장 (Square)"; // 기본값
    }

    private List<DailyScheduleItem> GetFallbackSchedule(string agentId)
    {
        var list = new List<DailyScheduleItem>();
        if (agentId == "npc_kyle")
        {
            list.Add(new DailyScheduleItem { StartHour = 0, EndHour = 7, TargetLocation = "성당 (Church)", Activity = "취침 및 휴식" });
            list.Add(new DailyScheduleItem { StartHour = 7, EndHour = 12, TargetLocation = "성당 (Church)", Activity = "아침 기도 및 예배 조율" });
            list.Add(new DailyScheduleItem { StartHour = 12, EndHour = 14, TargetLocation = "광장 (Square)", Activity = "산책 및 주민들과 교류" });
            list.Add(new DailyScheduleItem { StartHour = 14, EndHour = 19, TargetLocation = "성당 (Church)", Activity = "오후 예배 및 성전 청소" });
            list.Add(new DailyScheduleItem { StartHour = 19, EndHour = 22, TargetLocation = "술집 (Tavern)", Activity = "저녁 식사 및 음료 섭취" });
            list.Add(new DailyScheduleItem { StartHour = 22, EndHour = 23, TargetLocation = "성당 (Church)", Activity = "하루 묵상 및 취침 준비" });
        }
        else if (agentId == "npc_eva")
        {
            list.Add(new DailyScheduleItem { StartHour = 0, EndHour = 8, TargetLocation = "술집 (Tavern)", Activity = "취침 및 개인 정비" });
            list.Add(new DailyScheduleItem { StartHour = 8, EndHour = 11, TargetLocation = "광장 (Square)", Activity = "아침 장보기 및 가벼운 대화" });
            list.Add(new DailyScheduleItem { StartHour = 11, EndHour = 18, TargetLocation = "술집 (Tavern)", Activity = "낮 시간 개장 준비 및 맥주잔 닦기" });
            list.Add(new DailyScheduleItem { StartHour = 18, EndHour = 23, TargetLocation = "술집 (Tavern)", Activity = "저녁 시간 맥주 판매 및 손님들과 대화" });
        }
        else // npc_bart
        {
            list.Add(new DailyScheduleItem { StartHour = 0, EndHour = 8, TargetLocation = "술집 (Tavern)", Activity = "취침" });
            list.Add(new DailyScheduleItem { StartHour = 8, EndHour = 12, TargetLocation = "광장 (Square)", Activity = "훈련 및 무기 점검" });
            list.Add(new DailyScheduleItem { StartHour = 12, EndHour = 18, TargetLocation = "광장 (Square)", Activity = "경계 근무 및 마을 순찰" });
            list.Add(new DailyScheduleItem { StartHour = 18, EndHour = 23, TargetLocation = "술집 (Tavern)", Activity = "술 마시며 피로 해소" });
        }
        return list;
    }
}

// JSON 역직렬화용 DTO 레코드들
public record ReflectionResponse(
    [property: JsonPropertyName("core_facts")] List<CoreFactDto> CoreFacts
);

public record CoreFactDto(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("importance")] int Importance
);

public record ScheduleResponse(
    [property: JsonPropertyName("schedules")] List<ScheduleItemDto> Schedules
);

public record ScheduleItemDto(
    [property: JsonPropertyName("start_hour")] int StartHour,
    [property: JsonPropertyName("end_hour")] int EndHour,
    [property: JsonPropertyName("target_location")] string TargetLocation,
    [property: JsonPropertyName("activity")] string Activity
);
