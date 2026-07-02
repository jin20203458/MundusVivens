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

public class DialogueJob
{
    private static long _jobIdSequence = 0;
    public static ulong GenerateJobId() => (ulong)System.Threading.Interlocked.Increment(ref _jobIdSequence);

    public ulong JobId { get; } = GenerateJobId();
    public List<string> ParticipantIds { get; }
    public bool WaitForCompletion { get; }
    public TaskCompletionSource<DialogueSchedulerResult> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DialogueJob(List<string> participantIds, bool waitForCompletion)
    {
        ParticipantIds = participantIds;
        WaitForCompletion = waitForCompletion;
    }

    public DialogueJob(string agentIdA, string agentIdB, bool waitForCompletion)
    {
        ParticipantIds = new List<string> { agentIdA, agentIdB };
        WaitForCompletion = waitForCompletion;
    }
}

public class DialogueSchedulerResult
{
    public ulong JobId { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> DialogueLines { get; set; } = new();
    public List<DialogueLine> StructuredLines { get; set; } = new();
    public List<AgentEmotionUpdate> EmotionUpdates { get; set; } = new(); // 🆕 감정 업데이트 추가
    public List<NextJobDto> NextJobs { get; set; } = new(); // 🆕 대화 종료 후 공동 계획 수립 결과
}

public class InteractionScheduler : BackgroundService
{
    private readonly Channel<DialogueJob> _incomingChannel;
    private readonly ConcurrentQueue<DialogueJob> _pendingQueue = new();
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly IDialogueOrchestrator _orchestrator;
    private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
    private readonly IWorldEventBroadcaster _broadcaster;
    private readonly IPersistenceService _persistence;
    private readonly ILogger<InteractionScheduler> _logger;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<ulong, (DialogueSchedulerResult Result, DateTime CompletedAt)> _completedResults = new();

    public InteractionScheduler(
        IDialogueOrchestrator orchestrator,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
        IWorldEventBroadcaster broadcaster,
        IPersistenceService persistence,
        ILogger<InteractionScheduler> logger,
        int maxGlobalConcurrent = 10)
    {
        _orchestrator = orchestrator;
        _agentsAccessor = agentsAccessor;
        _broadcaster = broadcaster;
        _persistence = persistence;
        _logger = logger;
        _globalSemaphore = new SemaphoreSlim(maxGlobalConcurrent);

        _incomingChannel = Channel.CreateBounded<DialogueJob>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async Task<DialogueSchedulerResult> QueueDialogueJobAsync(string agentIdA, string agentIdB, bool wait, CancellationToken cancellationToken = default)
    {
        return await QueueGroupDialogueJobAsync(new List<string> { agentIdA, agentIdB }, wait, cancellationToken);
    }

    public async Task<DialogueSchedulerResult> QueueGroupDialogueJobAsync(List<string> participantIds, bool wait, CancellationToken cancellationToken = default)
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

        var job = new DialogueJob(participantIds, wait);
        
        _logger.LogInformation($"[Scheduler] 대화 등록 요청: {string.Join(", ", participantIds)} (wait={wait})");
        await _incomingChannel.Writer.WriteAsync(job, cancellationToken);

        if (wait)
        {
            return await job.CompletionSource.Task;
        }

        return new DialogueSchedulerResult
        {
            JobId = job.JobId,
            Success = true,
            Summary = "대화가 큐에 등록되었습니다. 백그라운드에서 진행됩니다."
        };
    }

    public List<object> GetActiveAndPendingJobs()
    {
        var agents = _agentsAccessor();
        var activeAgents = agents.Values.Where(a => a.Status.IsInConversation).Select(a => a.AgentId).ToList();

        return _pendingQueue.ToArray().Select(j => new
        {
            j.JobId,
            Participants = j.ParticipantIds,
            Status = j.ParticipantIds.Any(pId => activeAgents.Contains(pId)) ? "대기 중 (상대 에이전트 바쁨)" : "가용 (대기열 진입 준비 완료)"
        }).Cast<object>().ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Scheduler] Interaction Scheduler Background Worker 시작됨.");

        var readerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var job in _incomingChannel.Reader.ReadAllAsync(stoppingToken))
                {
                    _pendingQueue.Enqueue(job);
                    _logger.LogInformation($"[Scheduler] 새 작업 큐 추가: {job.JobId}. 대기 개수: {_pendingQueue.Count}");
                    
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
            var skippedJobs = new List<DialogueJob>();

            while (_pendingQueue.TryDequeue(out var job))
            {
                // Check if all participants are free
                bool anyBusy = false;
                foreach (var pId in job.ParticipantIds)
                {
                    if (!agents.TryGetValue(pId, out var agent) || agent.Status.IsInConversation)
                    {
                        anyBusy = true;
                        break;
                    }
                }

                if (anyBusy)
                {
                    skippedJobs.Add(job);
                    continue;
                }

                if (_globalSemaphore.Wait(0))
                {
                    // Set all participants to busy
                    foreach (var pId in job.ParticipantIds)
                    {
                        var agent = agents[pId];
                        agent.Status.IsInConversation = true;
                        agent.Status.Activity = "대화 중";
                    }

                    _ = Task.Run(async () =>
                    {
                        _logger.LogInformation($"[Scheduler] 대화 실행 시작: Job {job.JobId} ({string.Join(", ", job.ParticipantIds)})");
                        try
                        {
                            // Teleport other participants to the first participant's location
                            var primaryAgent = agents[job.ParticipantIds[0]];
                            for (int i = 1; i < job.ParticipantIds.Count; i++)
                            {
                                var otherAgent = agents[job.ParticipantIds[i]];
                                otherAgent.Status.CurrentLocation = primaryAgent.Status.CurrentLocation;
                            }

                            // 1. 대화 시작 이벤트 브로드캐스트
                            var startEvent = new MundusVivens.Prototype.Protos.WorldEvent
                            {
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                Dialogue = new MundusVivens.Prototype.Protos.DialogueEvent
                                {
                                    TaskId = job.JobId,
                                    AgentAId = AgentIdMapping.GetNumericId(job.ParticipantIds[0]),
                                    AgentBId = job.ParticipantIds.Count > 1 ? AgentIdMapping.GetNumericId(job.ParticipantIds[1]) : 0,
                                    Location = primaryAgent.Status.CurrentLocation,
                                    IsStarted = true
                                }
                            };
                            startEvent.Dialogue.ParticipantIds.AddRange(job.ParticipantIds.Select(AgentIdMapping.GetNumericId));
                            await _broadcaster.BroadcastAsync(startEvent);

                            // Run conversation: if 2 agents, use original. If more, use group
                            DialogueResult result;
                            if (job.ParticipantIds.Count == 2)
                            {
                                result = await _orchestrator.RunConversationAsync(agents[job.ParticipantIds[0]], agents[job.ParticipantIds[1]], job.JobId);
                            }
                            else
                            {
                                var participantsList = job.ParticipantIds.Select(pId => agents[pId]).ToList();
                                result = await _orchestrator.RunGroupConversationAsync(participantsList, job.JobId);
                            }
                            
                            var schedulerResult = new DialogueSchedulerResult
                            {
                                JobId = job.JobId,
                                Success = true,
                                Summary = result.Summary,
                                DialogueLines = result.DialogueLines,
                                StructuredLines = result.StructuredLines,
                                EmotionUpdates = result.EmotionUpdates,
                                NextJobs = result.NextJobs
                            };

                            _completedResults[job.JobId] = (schedulerResult, DateTime.UtcNow);
                            job.CompletionSource.TrySetResult(schedulerResult);

                            // 2. 대화 완료 이벤트 브로드캐스트
                            var endEvent = new MundusVivens.Prototype.Protos.WorldEvent
                            {
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                Dialogue = new MundusVivens.Prototype.Protos.DialogueEvent
                                {
                                    TaskId = job.JobId,
                                    AgentAId = AgentIdMapping.GetNumericId(job.ParticipantIds[0]),
                                    AgentBId = job.ParticipantIds.Count > 1 ? AgentIdMapping.GetNumericId(job.ParticipantIds[1]) : 0,
                                    Location = primaryAgent.Status.CurrentLocation,
                                    IsStarted = false,
                                    Summary = result.Summary
                                }
                            };
                            endEvent.Dialogue.ParticipantIds.AddRange(job.ParticipantIds.Select(AgentIdMapping.GetNumericId));
                            foreach (var line in result.StructuredLines)
                            {
                                endEvent.Dialogue.Lines.Add(line);
                            }
                            await _broadcaster.BroadcastAsync(endEvent);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[Scheduler] Job {job.JobId} 실행 중 예외 발생");
                            var errorResult = new DialogueSchedulerResult
                            {
                                JobId = job.JobId,
                                Success = false,
                                ErrorMessage = $"대화 실행 중 오류 발생: {ex.Message}"
                            };

                            _completedResults[job.JobId] = (errorResult, DateTime.UtcNow);
                            job.CompletionSource.TrySetResult(errorResult);
                        }
                        finally
                        {
                            foreach (var pId in job.ParticipantIds)
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
                            _logger.LogInformation($"[Scheduler] 대화 실행 완료 및 락 해제: Job {job.JobId}");

                            TriggerScheduling();
                        }
                    });
                }
                else
                {
                    _logger.LogWarning($"[Scheduler] 글로벌 실행 한도 초과로 작업을 연기합니다. 대기 중인 작업 개수: {skippedJobs.Count + 1}");
                    skippedJobs.Add(job);
                    break;
                }
            }

            // Re-enqueue skipped jobs
            foreach (var job in skippedJobs)
            {
                _pendingQueue.Enqueue(job);
            }
        }
    }

    public bool TryGetCompletedResult(ulong jobId, out DialogueSchedulerResult? result)
    {
        if (_completedResults.TryGetValue(jobId, out var cached))
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
}
