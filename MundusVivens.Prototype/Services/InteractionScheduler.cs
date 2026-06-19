using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MundusVivens.Prototype.Models;
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
    public string JobId { get; } = Guid.NewGuid().ToString("N");
    public string AgentIdA { get; }
    public string AgentIdB { get; }
    public bool WaitForCompletion { get; }
    public TaskCompletionSource<DialogueSchedulerResult> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DialogueJob(string agentIdA, string agentIdB, bool waitForCompletion)
    {
        AgentIdA = agentIdA;
        AgentIdB = agentIdB;
        WaitForCompletion = waitForCompletion;
    }
}

public class DialogueSchedulerResult
{
    public string JobId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> DialogueLines { get; set; } = new();
    public List<MundusVivens.Prototype.Protos.DialogueLine> StructuredLines { get; set; } = new();
}

public class InteractionScheduler : BackgroundService
{
    private readonly Channel<DialogueJob> _incomingChannel;
    private readonly List<DialogueJob> _pendingJobs = new();
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly IDialogueOrchestrator _orchestrator;
    private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
    private readonly IWorldEventBroadcaster _broadcaster;
    private readonly ILogger<InteractionScheduler> _logger;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, (DialogueSchedulerResult Result, DateTime CompletedAt)> _completedResults = new();

    public InteractionScheduler(
        IDialogueOrchestrator orchestrator,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
        IWorldEventBroadcaster broadcaster,
        ILogger<InteractionScheduler> logger,
        int maxGlobalConcurrent = 2)
    {
        _orchestrator = orchestrator;
        _agentsAccessor = agentsAccessor;
        _broadcaster = broadcaster;
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
        var agents = _agentsAccessor();
        if (!agents.ContainsKey(agentIdA) || !agents.ContainsKey(agentIdB))
        {
            return new DialogueSchedulerResult
            {
                Success = false,
                ErrorMessage = "존재하지 않는 에이전트 ID입니다."
            };
        }

        if (agentIdA == agentIdB)
        {
            return new DialogueSchedulerResult
            {
                Success = false,
                ErrorMessage = "동일한 에이전트끼리는 대화할 수 없습니다."
            };
        }

        var job = new DialogueJob(agentIdA, agentIdB, wait);
        
        _logger.LogInformation($"[Scheduler] 대화 등록 요청: {agentIdA} <-> {agentIdB} (wait={wait})");
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
        lock (_lock)
        {
            var agents = _agentsAccessor();
            var activeAgents = agents.Values.Where(a => a.Status.IsInConversation).Select(a => a.AgentId).ToList();

            return _pendingJobs.Select(j => new
            {
                j.JobId,
                j.AgentIdA,
                j.AgentIdB,
                Status = (activeAgents.Contains(j.AgentIdA) || activeAgents.Contains(j.AgentIdB)) ? "대기 중 (상대 에이전트 바쁨)" : "가용 (대기열 진입 준비 완료)"
            }).Cast<object>().ToList();
        }
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
                    lock (_lock)
                    {
                        _pendingJobs.Add(job);
                    }
                    _logger.LogInformation($"[Scheduler] 새 작업 큐 추가: {job.JobId}. 대기 개수: {_pendingJobs.Count}");
                    
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
            if (_pendingJobs.Count == 0) return;

            var agents = _agentsAccessor();
            
            for (int i = 0; i < _pendingJobs.Count; i++)
            {
                var job = _pendingJobs[i];
                var agentA = agents[job.AgentIdA];
                var agentB = agents[job.AgentIdB];

                if (agentA.Status.IsInConversation || agentB.Status.IsInConversation)
                {
                    continue;
                }

                if (_globalSemaphore.Wait(0))
                {
                    agentA.Status.IsInConversation = true;
                    agentB.Status.IsInConversation = true;
                    agentA.Status.Activity = $"{agentB.Persona.Name}와(과) 대화 중";
                    agentB.Status.Activity = $"{agentA.Persona.Name}와(과) 대화 중";

                    _pendingJobs.RemoveAt(i);
                    i--;

                    _ = Task.Run(async () =>
                    {
                        _logger.LogInformation($"[Scheduler] 대화 실행 시작: Job {job.JobId} ({agentA.Persona.Name} <-> {agentB.Persona.Name})");
                        try
                        {
                            agentB.Status.CurrentLocation = agentA.Status.CurrentLocation;

                            // 1. 대화 시작 이벤트 브로드캐스트
                            var startEvent = new MundusVivens.Prototype.Protos.WorldEvent
                            {
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                Dialogue = new MundusVivens.Prototype.Protos.DialogueEvent
                                {
                                    TaskId = job.JobId,
                                    AgentAId = job.AgentIdA,
                                    AgentBId = job.AgentIdB,
                                    Location = agentA.Status.CurrentLocation,
                                    IsStarted = true
                                }
                            };
                            await _broadcaster.BroadcastAsync(startEvent);

                            var result = await _orchestrator.RunConversationAsync(agentA, agentB, job.JobId);
                            
                            var schedulerResult = new DialogueSchedulerResult
                            {
                                JobId = job.JobId,
                                Success = true,
                                Summary = result.Summary,
                                DialogueLines = result.DialogueLines,
                                StructuredLines = result.StructuredLines
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
                                    AgentAId = job.AgentIdA,
                                    AgentBId = job.AgentIdB,
                                    Location = agentA.Status.CurrentLocation,
                                    IsStarted = false,
                                    Summary = result.Summary
                                }
                            };
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
                            agentA.Status.IsInConversation = false;
                            agentB.Status.IsInConversation = false;
                            agentA.Status.Activity = "대기 중";
                            agentB.Status.Activity = "대기 중";

                            _globalSemaphore.Release();
                            _logger.LogInformation($"[Scheduler] 대화 실행 완료 및 락 해제: Job {job.JobId}");

                            TriggerScheduling();
                        }
                    });
                }
                else
                {
                    _logger.LogWarning($"[Scheduler] 글로벌 실행 한도 초과로 작업을 연기합니다. 대기 중인 작업 개수: {_pendingJobs.Count}");
                    break;
                }
            }
        }
    }

    public bool TryGetCompletedResult(string jobId, out DialogueSchedulerResult? result)
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
