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
    private readonly LlmResponseLogger _llmResponseLogger;

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
        IPersistenceService persistenceService,
        LlmResponseLogger llmResponseLogger)
    {
        _apiService = apiService;
        _agentsAccessor = agentsAccessor;
        _persistenceService = persistenceService;
        _llmResponseLogger = llmResponseLogger;

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
                // 0. 원정 중 오토런(Auto-Continue) 스케줄 계산 및 LLM 우회 검사
                List<DailyScheduleItem> schedule;
                string? lastScheduledTarget = agent.CurrentSchedule?.LastOrDefault()?.TargetLocation;
                int hoursRemaining = 0;

                if (!string.IsNullOrEmpty(lastScheduledTarget))
                {
                    string currentLocParsed = LocationCoordinateRegistry.ParseLocation(agent.Status.CurrentLocation);
                    string targetLocParsed = LocationCoordinateRegistry.ParseLocation(lastScheduledTarget);
                    if (string.Equals(currentLocParsed, targetLocParsed, StringComparison.OrdinalIgnoreCase))
                    {
                        hoursRemaining = 0;
                    }
                    else
                    {
                        hoursRemaining = LocationCoordinateRegistry.GetTravelTimeHoursFromCoord(agent.Status.X, agent.Status.Y, agent.Status.Z, lastScheduledTarget);
                    }
                }

                if (hoursRemaining > 0)
                {
                    Console.WriteLine($"🚗 [Auto-Continue] 에이전트 '{agent.Persona.Name}'가 아직 목적지 '{lastScheduledTarget}'에 도착하지 못했습니다 (남은 예상 시간: {hoursRemaining}시간). LLM 호출 없이 원정 일정을 자동 연장합니다.");
                    schedule = new List<DailyScheduleItem>
                    {
                        new DailyScheduleItem
                        {
                            StartHour = 0,
                            EndHour = 23,
                            TargetLocation = lastScheduledTarget!,
                            Activity = $"{lastScheduledTarget}(으)로 이동"
                        }
                    };
                }
                else
                {
                    // 1. 성찰 및 다음 일정 통합 계획 생성 (Single-Shot)
                    schedule = await GenerateReflectedDailyPlanAsync(agent, cancellationToken);
                }

                // 2. 예비 버퍼(NextSchedule)에 보관
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

    private async Task<List<DailyScheduleItem>> GenerateReflectedDailyPlanAsync(AgentInstance agent, CancellationToken cancellationToken)
    {
        // 오늘 획득한 목격담 및 대화 사건 필터링
        var todayWitnessed = agent.MemoryBox.Beliefs.Values
            .Where(b => b.Type == BeliefType.Witnessed && (DateTime.UtcNow - b.AcquiredAt).TotalDays <= 1.0)
            .ToList();

        string episodesList = todayWitnessed.Any()
            ? string.Join("\n", todayWitnessed.Select(b => $"- [{b.AcquiredAt:HH:mm}] 사건내용: {b.Content}"))
            : "오늘 특별한 사건이나 대화가 관찰되지 않았습니다.";

        // 기존 인상 목록 추출
        var existingImpressions = string.Join("\n", agent.RelationshipMap.Values
            .Where(r => !string.IsNullOrEmpty(r.ImpressionSummary))
            .Select(r => $"- {r.TargetAgentId}: \"{r.ImpressionSummary}\""));

        // 중요도 순 Top-10 믿음 목록 추출
        var sortedBeliefs = agent.MemoryBox.Beliefs.Values
            .OrderByDescending(b => b.Importance)
            .Take(10)
            .ToList();

        string coreMemoriesList = sortedBeliefs.Any()
            ? string.Join("\n", sortedBeliefs.Select(b => $"- {b.Content} (중요도: {b.Importance:F2} - {PromptFormattingHelpers.GetImportanceLabel(b.Importance)})"))
            : "마음에 간직하고 있는 특별한 장기 기억이 없습니다.";

        // 요원의 현재 물리적 위치 좌표를 기반으로 LOD 마트료시카 위치 목록을 가져옴
        float ax = agent.Status.X;
        float az = agent.Status.Z;
        var lodLocations = LocationCoordinateRegistry.GetLodLocationList(ax, az);

        // 현재 위치하고 있는 장소명이 LOD 리스트에 명시적으로 없으면 추가
        string parsedCurrent = LocationCoordinateRegistry.ParseLocation(agent.Status.CurrentLocation);
        if (!lodLocations.Contains(parsedCurrent, StringComparer.OrdinalIgnoreCase))
        {
            lodLocations.Insert(0, parsedCurrent);
        }

        string locationList = string.Join("\n", lodLocations.Select(name => $"- {name}"));

        // LOD 장소 간 이동 소요 시간 매트릭스 구성
        var travelMatrixLines = new List<string>();
        for (int i = 0; i < lodLocations.Count; i++)
        {
            for (int j = i + 1; j < lodLocations.Count; j++)
            {
                int travelTime = LocationCoordinateRegistry.GetTravelTimeHours(lodLocations[i], lodLocations[j]);
                travelMatrixLines.Add($"- {lodLocations[i]} <-> {lodLocations[j]}: {travelTime}시간 소요");
            }
        }
        string travelMatrixText = string.Join("\n", travelMatrixLines);

        string prompt = $$"""
<role>가상 세계 NPC [{{agent.Persona.Name}}]의 자아성찰 및 하루 계획(Daily Schedule) 수립 시스템</role>
<task>오늘 하루 겪은 에피소드들을 분석하여 장기 기억과 당면 동기(Current Drive)를 갱신하고, 그 갱신된 동기를 즉각 반영하여 내일 하루(0시~23시) 동안의 구체적인 일정을 연이어 짜주십시오.</task>

<rules>
[1부: 자아 성찰 (Reflection)]
1. 오늘 겪은 단기 에피소드들을 종합하여 NPC가 깊이 깨닫거나 가치관에 반영할 장기 기억을 1~2개 도출하십시오.
2. 만약 오늘 대화 상대방(Target Agent)과 관련된 사건을 겪었다면, 그에 대한 전반적 인상(Impression Summary)을 업데이트하여 주십시오. 
   - 기존의 인상이 있었다면 이를 완전히 덮어씌우지 말고, 오늘 겪은 일을 누적 반영하여 결합/수정(Append/Revise)된 형태로 작성해야 합니다.
3. 당신의 궁극적인 장기 목표는 [{{agent.Persona.LongTermGoal}}]입니다. 오늘 하루 동안의 경험과 이 장기 목표를 고려하여, 내일 하루 동안 NPC가 최우선으로 추구해야 할 당면 동기(current_drive)를 구체적으로 수립하십시오.

[2부: 일정 수립 (Scheduling)]
4. 0시부터 23시까지의 일정이 빈 틈 없이 연속적이어야 합니다. (예: 0~7, 7~10, 10~15, 15~18, 18~22, 22~23)
5. NPC의 직업과 소속 진영(기억 및 신념 참고), 대화 상대들과의 관계를 일정에 자연스럽게 녹여내십시오. 
6. 목표 장소(target_location)는 반드시 [이동 가능한 장소 목록] 중 하나여야 합니다. (정확히 일치 필수)
   - 먼 지역(국가/도시)이 목록에 있을 때, 그 지역으로 여행/이동하고 싶다면 목록에 나온 국가명이나 도시명을 목표 장소로 그대로 사용해 주십시오. (예: "아르카디아 제국", "왕국 수도")
7. 24시간 계획을 3~6개의 시간대로 나누어 짜주십시오. 시작 시간과 종료 시간은 반드시 정수(0~23)여야 합니다.
8. 각 일정은 시작 시간, 종료 시간, 목표 장소, 해당 장소에서 할 구체적인 행동(activity)을 포함해야 합니다.
9. [중요] 다른 장소로 이동할 경우, 이동 소요 시간을 감안하여 일정을 계획하십시오. (이동 시간은 C# 시스템단에서 자동으로 정밀 보정됩니다)
10. [목표 의식 주입] 방금 1부에서 스스로 수립한 당면 동기(current_drive)를 실현하기 위한 적극적 행동(예: 정보원 만나기, 비밀 조사, 대화 시도 등)을 하루 일정 중 최소 한 타임 이상 반드시 일정에 반영하십시오.

[3부: 출력 형식]
11. 오직 아래 지정된 JSON 포맷의 데이터만 출력하십시오. 다른 텍스트 설명이나 주석은 절대 포함하지 마십시오.
</rules>

<context>
[이동 가능한 장소 목록]
{{locationList}}

[장소 간 이동 소요 시간]
{{travelMatrixText}}

[NPC 페르소나]
- 이름/직업: {{agent.Persona.Name}} / {{agent.Persona.Job}}
- 성격/말투: {{agent.Persona.ToneStyle}}
- 배경 이야기: {{agent.Persona.Backstory}}
- 핵심 가치관: {{agent.Persona.CoreValues}}
- 장기 목표: {{agent.Persona.LongTermGoal}}
- 현재 당면 동기: {{agent.Persona.CurrentDrive}}

[현재 감정 및 상태]
- 감정: {{agent.Status.Emotion}}
- 현재 위치: {{agent.Status.CurrentLocation}}

[기억하는 장기 기억들]
{{coreMemoriesList}}

[기존 대상별 관계 인상]
{{existingImpressions}}

[오늘 하루 동안 겪은 에피소드]
{{episodesList}}
</context>

<output_format>
반드시 아래 JSON 형식으로만 대답하십시오. 다른 텍스트 설명이나 주석은 절대 포함하지 마십시오.
{
  "reflection": {
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
    ],
    "current_drive": "내일 하루 동안 우선적으로 달성하고자 하는 단기적이고 구체적인 행동 동기"
  },
  "daily_schedule": [
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
            Contents: new List<Content> { new Content("user", new List<Part> { new Part("성찰 및 일정 계획 생성 시작.") }) },
            GenerationConfig: new GenerationConfig(null, 8192, "application/json", null, new ThinkingConfig(ThinkingLevel.low))
        );

        string responseJson = await _apiService.SendMessageAsync(request, ModelTier.Flash35, cancellationToken);

        // 🆕 디버그용 LLM 응답 저장
        await _llmResponseLogger.LogResponseAsync(agent.AgentId, "ReflectedSchedule", responseJson);

        var result = LlmJsonParser.DeserializeSafe<ReflectedScheduleResponse>(responseJson);
        if (result == null)
        {
            throw new Exception("LLM 합병 결과가 비어 있거나 올바른 포맷이 아닙니다.");
        }

        // --- 1부: 성찰 결과 처리 ---
        var reflection = result.Reflection;
        if (reflection != null)
        {
            if (reflection.ReflectionInsights != null)
            {
                foreach (var insight in reflection.ReflectionInsights)
                {
                    if (string.IsNullOrWhiteSpace(insight.Content)) continue;

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

            if (reflection.RelationshipUpdates != null)
            {
                foreach (var relUpdate in reflection.RelationshipUpdates)
                {
                    if (string.IsNullOrWhiteSpace(relUpdate.TargetAgentId) || string.IsNullOrWhiteSpace(relUpdate.NewImpression)) continue;

                    var rel = agent.RelationshipMap.GetOrAdd(relUpdate.TargetAgentId, id => new Relationship { TargetAgentId = id });
                    rel.ImpressionSummary = relUpdate.NewImpression;
                    Console.WriteLine($"👥 [Relationship Reflection] {agent.Persona.Name}이(가) {relUpdate.TargetAgentId}에 대한 인상을 갱신했습니다: \"{relUpdate.NewImpression}\"");
                }
            }

            if (!string.IsNullOrWhiteSpace(reflection.CurrentDrive))
            {
                agent.Persona.CurrentDrive = reflection.CurrentDrive;
                Console.WriteLine($"🎯 [Drive Reflection] {agent.Persona.Name}이(가) 당면 동기를 갱신했습니다: \"{reflection.CurrentDrive}\"");
            }
        }

        // --- 2부: 일정 수립 결과 처리 ---
        if (result.DailySchedule != null && result.DailySchedule.Count > 0)
        {
            var list = new List<DailyScheduleItem>();
            foreach (var item in result.DailySchedule)
            {
                string correctedLocation = MapToValidLocation(item.TargetLocation);
                list.Add(new DailyScheduleItem
                {
                    StartHour = item.StartHour,
                    EndHour = item.EndHour,
                    TargetLocation = correctedLocation,
                    Activity = item.Activity
                });
            }

            // 물리적 이동 시간 사후 검열 및 자동 보정
            var correctedList = PostProcessSchedule(list, agent.Status.CurrentLocation);
            return correctedList;
        }

        throw new Exception("LLM 일정 파싱 오류 또는 결과가 비어 있습니다.");
    }

    private List<DailyScheduleItem> PostProcessSchedule(List<DailyScheduleItem> rawSchedules, string currentAgentLocation)
    {
        if (rawSchedules == null || rawSchedules.Count == 0) return rawSchedules ?? new List<DailyScheduleItem>();

        // 1. 24시간 배열 초기화
        string[] targetLocations = new string[24];
        string[] activities = new string[24];

        // 기본값 채우기 (현재 위치 혹은 술집으로 채움)
        string fallbackLoc = !string.IsNullOrWhiteSpace(currentAgentLocation) 
            ? LocationCoordinateRegistry.ParseLocation(currentAgentLocation) 
            : "술집 (Pub)";
        for (int i = 0; i < 24; i++)
        {
            targetLocations[i] = fallbackLoc;
            activities[i] = "대기";
        }

        // LLM이 반환한 일정을 시간 슬롯에 매핑
        foreach (var item in rawSchedules)
        {
            int start = Math.Clamp(item.StartHour, 0, 23);
            int end = Math.Clamp(item.EndHour, 0, 23);
            if (start > end) continue;

            string loc = LocationCoordinateRegistry.ParseLocation(item.TargetLocation);
            for (int h = start; h <= end; h++)
            {
                targetLocations[h] = loc;
                activities[h] = item.Activity;
            }
        }

        // 2. 이동 시간 보정 (사후 검열)
        string[] finalLocations = (string[])targetLocations.Clone();
        string[] finalActivities = (string[])activities.Clone();

        string previousDayEndLocation = !string.IsNullOrWhiteSpace(currentAgentLocation)
            ? LocationCoordinateRegistry.ParseLocation(currentAgentLocation)
            : fallbackLoc;

        for (int h = 0; h < 24; h++)
        {
            string locA = (h == 0) ? previousDayEndLocation : targetLocations[h - 1];
            string locB = targetLocations[h];

            if (locA != locB)
            {
                int T = LocationCoordinateRegistry.GetTravelTimeHours(locA, locB);
                if (T > 0)
                {
                    if (h == 0)
                    {
                        // 0시에 시작하는 위치 전이의 경우, 새 날의 시작 부분(0 ~ T-1)을 이동 시간으로 채움
                        for (int fill = 0; fill < T; fill++)
                        {
                            if (fill < 24)
                            {
                                finalLocations[fill] = locB;
                                finalActivities[fill] = $"{locB}(으)로 이동";
                            }
                        }
                    }
                    else
                    {
                        // h시에 시작하는 위치 전이의 경우, 전 시간대(h - T ~ h - 1)를 이동 시간으로 채움
                        for (int fill = h - T; fill < h; fill++)
                        {
                            if (fill >= 0)
                            {
                                finalLocations[fill] = locB;
                                finalActivities[fill] = $"{locB}(으)로 이동";
                            }
                        }
                    }
                }
            }
        }

        // 3. 연속된 동일 활동 그룹화하여 다시 DailyScheduleItem 리스트로 변환
        var correctedSchedules = new List<DailyScheduleItem>();
        int groupStart = 0;
        for (int h = 1; h <= 24; h++)
        {
            if (h == 24 || 
                finalLocations[h] != finalLocations[groupStart] || 
                finalActivities[h] != finalActivities[groupStart])
            {
                correctedSchedules.Add(new DailyScheduleItem
                {
                    StartHour = groupStart,
                    EndHour = h - 1,
                    TargetLocation = finalLocations[groupStart],
                    Activity = finalActivities[groupStart]
                });
                groupStart = h;
            }
        }

        return correctedSchedules;
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
public record ReflectedScheduleResponse(
    [property: JsonPropertyName("reflection")] ReflectionResponse Reflection,
    [property: JsonPropertyName("daily_schedule")] List<ScheduleItemDto> DailySchedule
);

public record ReflectionResponse(
    [property: JsonPropertyName("core_facts")] List<ReflectionInsightDto> ReflectionInsights,
    [property: JsonPropertyName("relationship_updates")] List<RelationshipUpdateDto>? RelationshipUpdates,
    [property: JsonPropertyName("current_drive")] string? CurrentDrive
);

public record ReflectionInsightDto(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("importance")] int Importance
);

public record RelationshipUpdateDto(
    [property: JsonPropertyName("target_agent_id")] string TargetAgentId,
    [property: JsonPropertyName("new_impression")] string NewImpression
);

public record ScheduleItemDto(
    [property: JsonPropertyName("start_hour")] int StartHour,
    [property: JsonPropertyName("end_hour")] int EndHour,
    [property: JsonPropertyName("target_location")] string TargetLocation,
    [property: JsonPropertyName("activity")] string Activity
);

