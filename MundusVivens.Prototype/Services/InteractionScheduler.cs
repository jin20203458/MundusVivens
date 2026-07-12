using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Protos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public class DialogueTask
{
    private static long _taskIdSequence = 0;
    public static ulong GenerateTaskId() => (ulong)System.Threading.Interlocked.Increment(ref _taskIdSequence);

    public ulong TaskId { get; } = GenerateTaskId();
    public List<string> ParticipantIds { get; }
    public TaskCompletionSource<DialogueSchedulerResult> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DialogueTask(List<string> participantIds)
    {
        ParticipantIds = participantIds;
    }

    public DialogueTask(string agentIdA, string agentIdB)
    {
        ParticipantIds = new List<string> { agentIdA, agentIdB };
    }
}

public class DialogueSchedulerResult
{
    public ulong TaskId { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<DialogueLine> StructuredLines { get; set; } = new();
    public List<AgentEmotionUpdate> EmotionUpdates { get; set; } = new(); // 🆕 감정 업데이트 추가
    public List<NextJobDto> NextJobs { get; set; } = new(); // 🆕 대화 종료 후 공동 계획 수립 결과
    public List<string> Keywords { get; set; } = new(); // 🆕 대화 키워드 추가
}

public class InteractionScheduler : BackgroundService
{
    private readonly Channel<DialogueTask> _incomingChannel;
    private readonly ConcurrentQueue<DialogueTask> _pendingQueue = new();
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly IDialogueOrchestrator _orchestrator;
    private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
    private readonly IPersistenceService _persistence;
    private readonly ILogger<InteractionScheduler> _logger;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<ulong, (DialogueSchedulerResult Result, DateTime CompletedAt)> _completedResults = new();

    public InteractionScheduler(
        IDialogueOrchestrator orchestrator,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
        IPersistenceService persistence,
        ILogger<InteractionScheduler> logger,
        int maxGlobalConcurrent = 10)
    {
        _orchestrator = orchestrator;
        _agentsAccessor = agentsAccessor;
        _persistence = persistence;
        _logger = logger;
        _globalSemaphore = new SemaphoreSlim(maxGlobalConcurrent);

        _incomingChannel = Channel.CreateBounded<DialogueTask>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async Task<DialogueSchedulerResult> QueueDialogueTaskAsync(string agentIdA, string agentIdB, CancellationToken cancellationToken = default)
    {
        return await QueueGroupDialogueTaskAsync(new List<string> { agentIdA, agentIdB }, cancellationToken);
    }

    public async Task<DialogueSchedulerResult> QueueGroupDialogueTaskAsync(List<string> participantIds, CancellationToken cancellationToken = default)
    {
        var agents = _agentsAccessor();
        foreach (var pId in participantIds)
        {
            if (!agents.ContainsKey(pId))
            {
                return new DialogueSchedulerResult
                {
                    Success = false,
                    ErrorMessage = $"존재하지 않는 에이전트 ID입니다: {pId}"
                };
            }
        }

        if (participantIds.Distinct().Count() != participantIds.Count)
        {
            return new DialogueSchedulerResult
            {
                Success = false,
                ErrorMessage = "중복된 에이전트가 포함되어 있습니다."
            };
        }

        if (participantIds.Count < 2)
        {
            return new DialogueSchedulerResult
            {
                Success = false,
                ErrorMessage = "대화에 참여하려면 최소 2명의 에이전트가 필요합니다."
            };
        }

        var task = new DialogueTask(participantIds);
        
        _logger.LogInformation($"[Scheduler] 대화 등록 요청: {string.Join(", ", participantIds)}");
        await _incomingChannel.Writer.WriteAsync(task, cancellationToken);

        return await task.CompletionSource.Task;
    }

    public List<object> GetActiveAndPendingTasks()
    {
        var agents = _agentsAccessor();
        var activeAgents = agents.Values.Where(a => a.Status.IsInConversation).Select(a => a.AgentId).ToList();

        return _pendingQueue.ToArray().Select(t => new
        {
            t.TaskId,
            Participants = t.ParticipantIds,
            Status = t.ParticipantIds.Any(pId => activeAgents.Contains(pId)) ? "대기 중 (상대 에이전트 바쁨)" : "가용 (대기열 진입 준비 완료)"
        }).Cast<object>().ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Scheduler] Interaction Scheduler Background Worker 시작됨.");

        var readerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var task in _incomingChannel.Reader.ReadAllAsync(stoppingToken))
                {
                    _pendingQueue.Enqueue(task);
                    _logger.LogInformation($"[Scheduler] 새 작업 큐 추가: {task.TaskId}. 대기 개수: {_pendingQueue.Count}");
                    
                    TriggerScheduling();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler] 채널 리더 태스크 예외 발생");
            }
        }, stoppingToken);

        int cleanupCounter = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            TriggerScheduling();

            cleanupCounter++;
            if (cleanupCounter >= 30) // 30 seconds interval
            {
                cleanupCounter = 0;
                CleanupExpiredResults();
            }
        }

        await readerTask;
    }

    private void TriggerScheduling()
    {
        lock (_lock)
        {
            if (_pendingQueue.IsEmpty) return;

            var agents = _agentsAccessor();
            var skippedTasks = new List<DialogueTask>();

            while (_pendingQueue.TryDequeue(out var task))
            {
                // Check if all participants are free
                bool anyBusy = false;
                foreach (var pId in task.ParticipantIds)
                {
                    if (!agents.TryGetValue(pId, out var agent) || agent.Status.IsInConversation)
                    {
                        anyBusy = true;
                        break;
                    }
                }

                if (anyBusy)
                {
                    skippedTasks.Add(task);
                    continue;
                }

                if (_globalSemaphore.Wait(0))
                {
                    // Set all participants to busy
                    foreach (var pId in task.ParticipantIds)
                    {
                        var agent = agents[pId];
                        agent.Status.IsInConversation = true;
                        agent.Status.Activity = "대화 중";
                    }

                    _ = Task.Run(async () =>
                    {
                        _logger.LogInformation($"[Scheduler] 대화 실행 시작: Task {task.TaskId} ({string.Join(", ", task.ParticipantIds)})");
                        try
                        {
                            // Teleport other participants to the first participant's location
                            var primaryAgent = agents[task.ParticipantIds[0]];
                            for (int i = 1; i < task.ParticipantIds.Count; i++)
                            {
                                var otherAgent = agents[task.ParticipantIds[i]];
                                otherAgent.Status.CurrentLocation = primaryAgent.Status.CurrentLocation;
                            }

                            // Run conversation: if 2 agents, use original. If more, use group
                            DialogueResult result;
                            if (task.ParticipantIds.Count == 2)
                            {
                                result = await _orchestrator.RunConversationAsync(agents[task.ParticipantIds[0]], agents[task.ParticipantIds[1]], task.TaskId);
                            }
                            else
                            {
                                var participantsList = task.ParticipantIds.Select(pId => agents[pId]).ToList();
                                result = await _orchestrator.RunGroupConversationAsync(participantsList, task.TaskId);
                            }
                            
                            var schedulerResult = new DialogueSchedulerResult
                            {
                                TaskId = task.TaskId,
                                Success = true,
                                Summary = result.Summary,
                                StructuredLines = result.StructuredLines,
                                EmotionUpdates = result.EmotionUpdates,
                                NextJobs = result.NextJobs,
                                Keywords = result.Keywords
                            };

                            _completedResults[task.TaskId] = (schedulerResult, DateTime.UtcNow);
                            task.CompletionSource.TrySetResult(schedulerResult);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[Scheduler] Task {task.TaskId} 실행 중 예외 발생");
                            var errorResult = new DialogueSchedulerResult
                            {
                                TaskId = task.TaskId,
                                Success = false,
                                ErrorMessage = $"대화 실행 중 오류 발생: {ex.Message}"
                            };

                            _completedResults[task.TaskId] = (errorResult, DateTime.UtcNow);
                            task.CompletionSource.TrySetResult(errorResult);
                        }
                        finally
                        {
                            foreach (var pId in task.ParticipantIds)
                            {
                                if (agents.TryGetValue(pId, out var agent))
                                {
                                    agent.Status.IsInConversation = false;
                                    agent.Status.Activity = "대기 중";

                                    try
                                    {
                                        _persistence.UpsertAgent(agent);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, $"[Scheduler] DB 저장 중 에러 발생: {agent.AgentId}");
                                    }
                                }
                            }

                            _globalSemaphore.Release();
                            _logger.LogInformation($"[Scheduler] 대화 실행 완료 및 락 해제: Task {task.TaskId}");

                            TriggerScheduling();
                        }
                    });
                }
                else
                {
                    _logger.LogWarning($"[Scheduler] 글로벌 실행 한도 초과로 작업을 연기합니다. 대기 중인 작업 개수: {skippedTasks.Count + 1}");
                    skippedTasks.Add(task);
                    break;
                }
            }

            // Re-enqueue skipped tasks
            foreach (var skippedTask in skippedTasks)
            {
                _pendingQueue.Enqueue(skippedTask);
            }
        }
    }

    public bool TryGetCompletedResult(ulong taskId, out DialogueSchedulerResult? result)
    {
        if (_completedResults.TryGetValue(taskId, out var cached))
        {
            result = cached.Result;
            return true;
        }
        result = null;
        return false;
    }

    private void CleanupExpiredResults()
    {
        var now = DateTime.UtcNow;
        var expirationTime = TimeSpan.FromMinutes(5);

        foreach (var kvp in _completedResults)
        {
            if (now - kvp.Value.CompletedAt > expirationTime)
            {
                _completedResults.TryRemove(kvp.Key, out _);
                _logger.LogInformation($"[Scheduler] Expired dialogue result removed from cache: {kvp.Key}");
            }
        }
    }

    // 🆕 4단계: 적대 억제 및 행동 분기 결정 확률 모델
    public async Task<int> DetermineThreatActionAsync(AgentInstance agent, AgentInstance threatAgent)
    {
        _logger.LogInformation($"[ThreatInhibitor] NPC '{agent.Persona.Name}'가 '{threatAgent.Persona.Name}'를 감지하고 충동 억제를 시도합니다.");

        // 1. 관계 확인
        int liking = 0;
        int trust = 50;
        if (agent.RelationshipMap.TryGetValue(threatAgent.AgentId, out var rel))
        {
            liking = rel.Liking;
            trust = rel.Trust;
        }

        // 2. 성격 지표 (외향성 Extroversion -> 공격적/다혈질 성향으로 활용)
        double aggressiveness = agent.Persona.Extroversion;

        // 3. 누적 트라우마(공격당한 기억) 검색
        bool hasTrauma = agent.MemoryBox.Beliefs.Values.Any(b => b.SourceAgentId == threatAgent.AgentId && b.EmotionalCharge > 0.7);

        // 4. 위협 분석 점수 (Aggression Score)
        double attackWeight = 0.0;
        
        if (liking < 0) attackWeight += Math.Abs(liking) * 0.5; // 최대 +50
        if (trust < 30) attackWeight += (50 - trust) * 0.4;    // 최대 +20
        attackWeight += aggressiveness * 30.0;                 // 최대 +30
        if (hasTrauma) attackWeight += 40.0;                   // 보복 심리 +40

        _logger.LogInformation($"[ThreatInhibitor] '{agent.Persona.Name}' -> '{threatAgent.Persona.Name}' 위협 수치: {attackWeight:F1}");

        // 결정 분기:
        // 가중치 70 이상: APPROVE = 0 (즉시 무기 드로우 선제공격)
        // 가중치 30 ~ 70: SOCIALIZE = 2 (소셜 위협 대화 - 욕설/언쟁)
        // 가중치 30 미만: REJECT = 1 (억제 유지 - 회피/침묵)
        
        if (attackWeight >= 70.0)
        {
            _logger.LogInformation($"[ThreatInhibitor] '{agent.Persona.Name}'가 이성을 잃고 공격(APPROVE)을 승인합니다.");
            return 0; // APPROVE
        }
        else if (attackWeight >= 30.0)
        {
            _logger.LogInformation($"[ThreatInhibitor] '{agent.Persona.Name}'가 말싸움(SOCIALIZE)을 결심합니다.");
            
            // 소셜 위협(시비) 대화 Task를 백그라운드 DialogueQueue에 삽입
            _ = Task.Run(async () =>
            {
                await Task.Delay(200); 
                await QueueDialogueTaskAsync(agent.AgentId, threatAgent.AgentId);
            });

            return 2; // SOCIALIZE
        }
        else
        {
            _logger.LogInformation($"[ThreatInhibitor] '{agent.Persona.Name}'가 위협을 억제하고 회피(REJECT)합니다.");
            return 1; // REJECT
        }
    }
}
