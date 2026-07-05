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
    void EnqueueReflection(string agentId, int remainingTicks);
    void TrySwapNextSchedule(string agentId, int currentTick);
    bool IsAgentBusy(string agentId);
    List<DailyScheduleItem> GetScheduleForAgent(string agentId);
    void InitializeDefaultSchedules();
}

public class DailyPlanService : IDailyPlanService, IDisposable
{
    private readonly IGeminiApiService _apiService;
    private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
    private readonly IPersistenceService _persistenceService;

    // 🆕 비동기 스케줄 큐 및 워커를 위한 동기화 객체들
    private readonly object _queueLock = new();
    private readonly PriorityQueue<string, int> _taskQueue = new();
    private readonly HashSet<string> _queuedAgentIds = new();
    private readonly SemaphoreSlim _queueSemaphore = new(0);
    private readonly SemaphoreSlim _concurrencySemaphore = new(5); // 최대 5개 동시 병렬 요청
    private readonly CancellationTokenSource _workerTokenSource;
    private readonly Task _queueWorkerTask;

    public DailyPlanService(
        IGeminiApiService apiService,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
        IPersistenceService persistenceService)
    {
        _apiService = apiService;
        _agentsAccessor = agentsAccessor;
        _persistenceService = persistenceService;

        // 큐 워커 백그라운드 태스크 시작
        _workerTokenSource = new CancellationTokenSource();
        _queueWorkerTask = Task.Run(() => RunQueueWorkerAsync(_workerTokenSource.Token));
    }

    public void EnqueueReflection(string agentId, int remainingTicks)
    {
        var agents = _agentsAccessor();
        if (!agents.TryGetValue(agentId, out var agent)) return;

        lock (_queueLock)
        {
            // 이미 큐에 있거나 이미 다음 일정이 준비되어 있으면 무시
            if (_queuedAgentIds.Contains(agentId) || (agent.NextSchedule != null && agent.NextSchedule.Count > 0))
            {
                return;
            }

            _taskQueue.Enqueue(agentId, remainingTicks);
            _queuedAgentIds.Add(agentId);
            _queueSemaphore.Release();

            Console.WriteLine($"📥 [Queue] 에이전트 '{agent.Persona.Name}' 성찰 및 다음 계획 요청이 큐에 삽입되었습니다. (남은 물리 틱: {remainingTicks})");
        }
    }

    public bool IsAgentBusy(string agentId)
    {
        lock (_queueLock)
        {
            return _queuedAgentIds.Contains(agentId);
        }
    }

    public void TrySwapNextSchedule(string agentId, int currentTick)
    {
        var agents = _agentsAccessor();
        if (!agents.TryGetValue(agentId, out var agent)) return;

        // 만약 만료 틱에 도달했고 다음 계획이 준비되어 있다면 스왑!
        if (currentTick >= agent.PlanExpirationTick && agent.NextSchedule != null && agent.NextSchedule.Count > 0)
        {
            agent.CurrentSchedule = new List<DailyScheduleItem>(agent.NextSchedule);
            agent.PlanExpirationTick = currentTick + 24; // 새로운 24시간 만료 틱 지정
            agent.NextSchedule = null;

            // LiteDB 영구 저장
            _persistenceService.UpsertAgent(agent);

            Console.WriteLine($"🔄 [Schedule Swap] 에이전트 '{agent.Persona.Name}'의 일과 스케줄이 교체되었습니다. (새 만료 틱: {agent.PlanExpirationTick})");
        }
    }

    private async Task RunQueueWorkerAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[DailyPlanService] 백그라운드 성찰/스케줄 큐 워커가 구동되었습니다.");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _queueSemaphore.WaitAsync(cancellationToken);
                
                string? agentId = null;
                lock (_queueLock)
                {
                    if (!_taskQueue.TryDequeue(out var dequeuedId, out _) || dequeuedId == null)
                    {
                        continue;
                    }
                    agentId = dequeuedId;
                }

                // 초당 최대 10회 (TPS = 10) 속도 제어를 위해 디큐 사이에 100ms 딜레이 부여
                await Task.Delay(100, cancellationToken);

                // 동시 처리 제한 세마포어 획득 후 백그라운드 계산 실행
                await _concurrencySemaphore.WaitAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessReflectionAndScheduleForAgentAsync(agentId, cancellationToken);
                    }
                    finally
                    {
                        _concurrencySemaphore.Release();
                        lock (_queueLock)
                        {
                            _queuedAgentIds.Remove(agentId);
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Queue Worker Error] 큐 워커 루프 예외 발생: {ex.Message}");
            }
        }
    }

    private async Task ProcessReflectionAndScheduleForAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        var agents = _agentsAccessor();
        if (!agents.TryGetValue(agentId, out var agent)) return;

        int attempt = 0;
        int delayMs = 2000;
        while (attempt < 5)
        {
            try
            {
                Console.WriteLine($"🧠 [Queue Worker] 에이전트 '{agent.Persona.Name}' 성찰 및 다음 일정 계산 시작...");

                // 1. 자아성찰 수행
                await ReflectOnEpisodesAsync(agent, cancellationToken);

                // 2. 내일의 Daily Plan 생성
                var schedule = await GenerateDailyPlanAsync(agent, cancellationToken);

                // 3. 예비 버퍼(NextSchedule)에 보관
                agent.NextSchedule = schedule;

                // DB 저장
                _persistenceService.UpsertAgent(agent);

                Console.WriteLine($"🧠 [Queue Worker] 에이전트 '{agent.Persona.Name}' 성찰 및 다음 일정 수립 완료. 예비 버퍼(NextSchedule)에 임시 보관됨.");
                break;
            }
            catch (Exception ex) when (ex.Message.Contains("429") || ex.Message.Contains("Quota"))
            {
                attempt++;
                Console.WriteLine($"⚠️ [Queue Worker] 에이전트 '{agent.Persona.Name}' API 429(Rate Limit) 감지 (시도 {attempt}/5). {delayMs}ms 후 재시도... 에러: {ex.Message}");
                await Task.Delay(delayMs, cancellationToken);
                delayMs *= 2; // 지수 백오프
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Queue Worker Error] 에이전트 '{agent.Persona.Name}' 성찰 실패: {ex.Message}");
                break;
            }
        }
    }

    public void InitializeDefaultSchedules()
    {
        var agents = _agentsAccessor();
        foreach (var agent in agents.Values)
        {
            if (agent.AgentId == "player") continue;

            if (agent.CurrentSchedule == null || agent.CurrentSchedule.Count == 0)
            {
                if (agent.InitialSchedule != null && agent.InitialSchedule.Count > 0)
                {
                    agent.CurrentSchedule = new List<DailyScheduleItem>(agent.InitialSchedule);
                    var lastItem = agent.InitialSchedule.OrderByDescending(i => i.EndHour).FirstOrDefault();
                    agent.PlanExpirationTick = lastItem != null ? lastItem.EndHour : 23;

                    _persistenceService.UpsertAgent(agent);
                    Console.WriteLine($"[DailyPlanService] Loaded pre-authored Day 1 InitialSchedule for {agent.Persona.Name} (Expiration Tick: {agent.PlanExpirationTick})");
                }
                else
                {
                    var fallback = GetFallbackSchedule(agent.AgentId);
                    agent.CurrentSchedule = fallback;
                    agent.PlanExpirationTick = 23;

                    _persistenceService.UpsertAgent(agent);
                    Console.WriteLine($"[DailyPlanService] Loaded fallback schedule for {agent.Persona.Name} (Expiration Tick: {agent.PlanExpirationTick})");
                }
            }
        }
    }

    public List<DailyScheduleItem> GetScheduleForAgent(string agentId)
    {
        var agents = _agentsAccessor();
        if (agents.TryGetValue(agentId, out var agent))
        {
            if (agent.CurrentSchedule == null || agent.CurrentSchedule.Count == 0)
            {
                InitializeDefaultSchedules();
            }
            return agent.CurrentSchedule ?? GetFallbackSchedule(agentId);
        }

        return GetFallbackSchedule(agentId);
    }

    public void Dispose()
    {
        _workerTokenSource.Cancel();
        try
        {
            _queueWorkerTask.Wait(1000);
        }
        catch { }
        _workerTokenSource.Dispose();
        _queueSemaphore.Dispose();
        _concurrencySemaphore.Dispose();
    }

    private async Task ReflectOnEpisodesAsync(AgentInstance agent, CancellationToken cancellationToken)
    {
        // 오늘 획득한 목격담 및 대화 사건 필터링
        var todayWitnessed = agent.MemoryBox.Beliefs.Values
            .Where(b => b.Type == BeliefType.Witnessed && (DateTime.UtcNow - b.AcquiredAt).TotalDays <= 1.0)
            .ToList();

        if (todayWitnessed.Count == 0)
        {
            Console.WriteLine($"[Reflection] 에이전트 '{agent.Persona.Name}'는 오늘 특별한 사건(대화)이 없어 일상 요약 성찰을 진행합니다.");
            return;
        }

        string episodesList = string.Join("\n", todayWitnessed.Select(b => $"- [{b.AcquiredAt:HH:mm}] 사건내용: {b.Content}"));

        // 기존 인상 목록 추출
        var existingImpressions = string.Join("\n", agent.RelationshipMap.Values
            .Where(r => !string.IsNullOrEmpty(r.ImpressionSummary))
            .Select(r => $"- {r.TargetAgentId}: \"{r.ImpressionSummary}\""));

        string prompt = $$"""
<role>가상 세계 NPC [{{agent.Persona.Name}}]의 자아성찰(Reflection) 시스템</role>
<task>오늘 하루 동안 겪은 에피소드들을 분석하여, 캐릭터의 장기 기억(CoreFact)을 추출하고 상대방에 대한 인상(Impression)을 업데이트하십시오.</task>

<rules>
1. 오늘 겪은 단기 에피소드들을 종합하여 NPC가 깊이 깨닫거나 가치관에 반영할 장기 기억을 1~2개 도출하십시오.
2. 만약 오늘 대화 상대방(Target Agent)과 관련된 사건을 겪었다면, 그에 대한 전반적 인상(Impression Summary)을 업데이트하여 주십시오. 
   - 기존의 인상이 있었다면 이를 완전히 덮어씌우지 말고, 오늘 겪은 일을 누적 반영하여 결합/수정(Append/Revise)된 형태로 작성해야 합니다.
3. 오직 아래 지정된 JSON 포맷의 데이터만 출력하십시오.
</rules>

<context>
[NPC 페르소나]
- 이름/직업: {{agent.Persona.Name}} / {{agent.Persona.Job}}
- 성격/말투: {{agent.Persona.ToneStyle}}
- 배경 이야기: {{agent.Persona.Backstory}}
- 핵심 가치관: {{agent.Persona.CoreValues}}

[기존 대상별 관계 인상]
{{existingImpressions}}

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
  ],
  "relationship_updates": [
    {
      "target_agent_id": "npc_eva",
      "new_impression": "나에게 거짓말을 하는 등 최근 들어 영 미덥지 못해 경계 중임"
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

        if (reflectionResult?.ReflectionInsights != null)
        {
            foreach (var insight in reflectionResult.ReflectionInsights)
            {
                if (string.IsNullOrWhiteSpace(insight.Content)) continue;

                // 새로운 깨달음을 Witnessed 타입의 Belief로 통합 저장 (성찰 기억)
                var reflectionBelief = new Belief
                {
                    BeliefId = $"belief_reflection_{Guid.NewGuid().ToString().Substring(0, 5)}",
                    SubjectId = agent.AgentId,
                    Content = insight.Content,
                    Type = BeliefType.Witnessed,
                    Confidence = Math.Clamp(insight.Importance / 10.0, 0.1, 1.0),
                    Salience = 1.0,
                    EmotionalCharge = Math.Clamp((insight.Importance / 10.0) * 0.5, 0.0, 1.0),
                    AcquiredAt = DateTime.UtcNow
                };

                agent.MemoryBox.AddOrUpdateBelief(reflectionBelief);
                Console.WriteLine($"🧠 [Memory Reflection] {agent.Persona.Name}에게 성찰 기억 추가: \"{insight.Content}\" (중요도: {insight.Importance})");
            }
        }

        if (reflectionResult?.RelationshipUpdates != null)
        {
            foreach (var relUpdate in reflectionResult.RelationshipUpdates)
            {
                if (string.IsNullOrWhiteSpace(relUpdate.TargetAgentId) || string.IsNullOrWhiteSpace(relUpdate.NewImpression)) continue;

                var rel = agent.RelationshipMap.GetOrAdd(relUpdate.TargetAgentId, id => new Relationship { TargetAgentId = id });
                rel.ImpressionSummary = relUpdate.NewImpression;
                Console.WriteLine($"👥 [Relationship Reflection] {agent.Persona.Name}이(가) {relUpdate.TargetAgentId}에 대한 인상을 갱신했습니다: \"{relUpdate.NewImpression}\"");
            }
        }
    }

    private async Task<List<DailyScheduleItem>> GenerateDailyPlanAsync(AgentInstance agent, CancellationToken cancellationToken)
    {
        // 중요도 순 Top-10 믿음 목록 추출
        var sortedBeliefs = agent.MemoryBox.Beliefs.Values
            .OrderByDescending(b => b.Importance)
            .Take(10)
            .ToList();

        string coreMemoriesList = sortedBeliefs.Any()
            ? string.Join("\n", sortedBeliefs.Select(b => $"- {b.Content} (중요도: {b.Importance:F2} - {PromptFormattingHelpers.GetImportanceLabel(b.Importance)})"))
            : "마음에 간직하고 있는 특별한 장기 기억이 없습니다.";

        string locationList = LocationCoordinateRegistry.GetPromptLocationList();

        string prompt = $$"""
<role>NPC [{{agent.Persona.Name}}]의 하루 계획(Daily Schedule) 수립 시스템</role>
<task>NPC의 페르소나, 현재 상태, 관계도, 장기 기억을 고려하여 내일 하루(0시~23시) 동안의 구체적인 일정을 짜주십시오.</task>

<rules>
1. 0시부터 23시까지의 일정이 빈 틈 없이 연속적이어야 합니다. (예: 0~7, 7~10, 10~15, 15~18, 18~22, 22~23)
2. NPC의 직업과 소속 진영(기억 및 신념 참고), 대화 상대들과의 관계를 일정에 자연스럽게 녹여내십시오. 
3. 목표 장소(target_location)는 반드시 [이동 가능한 장소 목록] 중 하나여야 합니다. (정확히 일치 필수)
4. 24시간 계획을 3~6개의 시간대로 나누어 짜주십시오. 시작 시간과 종료 시간은 반드시 정수(0~23)여야 합니다.
5. 각 일정은 시작 시간, 종료 시간, 목표 장소, 해당 장소에서 할 구체적인 행동(activity)을 포함해야 합니다.
6. 오직 아래 지정된 JSON 포맷의 데이터만 출력하십시오.
</rules>

<context>
[이동 가능한 장소 목록]
{{locationList}}

[NPC 페르소나]
- 이름/직업: {{agent.Persona.Name}} / {{agent.Persona.Job}}
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
        return LocationCoordinateRegistry.ParseLocation(rawLocation);
    }

    private List<DailyScheduleItem> GetFallbackSchedule(string agentId)
    {
        var list = new List<DailyScheduleItem>();
        var agents = _agentsAccessor();
        string location = "광장 (Square)";
        
        if (agents.TryGetValue(agentId, out var agent))
        {
            location = agent.Status.CurrentLocation;
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            location = LocationCoordinateRegistry.GetAllSemanticNames().FirstOrDefault() ?? "Unknown";
        }

        list.Add(new DailyScheduleItem { StartHour = 0, EndHour = 8, TargetLocation = location, Activity = "취침 및 개인 정비" });
        list.Add(new DailyScheduleItem { StartHour = 8, EndHour = 18, TargetLocation = location, Activity = "일상 활동 수행 및 대기" });
        list.Add(new DailyScheduleItem { StartHour = 18, EndHour = 23, TargetLocation = location, Activity = "저녁 휴식 및 대기" });
        
        return list;
    }
}

// JSON 역직렬화용 DTO 레코드들
public record ReflectionResponse(
    [property: JsonPropertyName("core_facts")] List<ReflectionInsightDto> ReflectionInsights,
    [property: JsonPropertyName("relationship_updates")] List<RelationshipUpdateDto>? RelationshipUpdates
);

public record ReflectionInsightDto(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("importance")] int Importance
);

public record RelationshipUpdateDto(
    [property: JsonPropertyName("target_agent_id")] string TargetAgentId,
    [property: JsonPropertyName("new_impression")] string NewImpression
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
