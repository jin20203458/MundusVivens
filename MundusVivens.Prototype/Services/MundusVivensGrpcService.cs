using Grpc.Core;
using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Protos;
using System;
using System.Text.Json.Serialization;
using MundusVivens.Prototype.Helpers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public static class RelationshipChangeTracker
{
    private static readonly ConcurrentQueue<RelationshipDelta> _pendingDeltas = new();

    public static void TrackChange(uint fromId, uint toId, int liking, int trust)
    {
        _pendingDeltas.Enqueue(new RelationshipDelta
        {
            FromAgentId = fromId,
            ToAgentId = toId,
            Liking = liking,
            Trust = trust
        });
    }

    public static List<RelationshipDelta> DrainDeltas()
    {
        var list = new List<RelationshipDelta>();
        while (_pendingDeltas.TryDequeue(out var delta))
        {
            list.Add(delta);
        }
        return list;
    }
}

public class MundusVivensGrpcService : MundusVivensGrpc.MundusVivensGrpcBase
{
    private readonly InteractionScheduler _scheduler;
    private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
    private readonly IPlayerDialogueManager _playerDialogueManager;
    private readonly IDailyPlanService _dailyPlanService; // 🆕 일일 스케줄 및 성찰 서비스 추가
    private readonly IBeliefEngine _beliefEngine;
    private readonly IGeminiApiService _apiService;
    private readonly IPersistenceService _persistenceService;

    public MundusVivensGrpcService(
        InteractionScheduler scheduler,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
        IPlayerDialogueManager playerDialogueManager,
        IDailyPlanService dailyPlanService,
        IBeliefEngine beliefEngine,
        IGeminiApiService apiService,
        IPersistenceService persistenceService)
    {
        _scheduler = scheduler;
        _agentsAccessor = agentsAccessor;
        _playerDialogueManager = playerDialogueManager;
        _dailyPlanService = dailyPlanService;
        _beliefEngine = beliefEngine;
        _apiService = apiService;
        _persistenceService = persistenceService;
    }

    public override async Task<TriggerDialogueResponse> TriggerDialogue(TriggerDialogueRequest request, ServerCallContext context)
    {
        try
        {
            DialogueSchedulerResult result;
            if (request.ParticipantIds.Count > 0)
            {
                var stringIds = request.ParticipantIds.Select(AgentIdMapping.GetStringId).ToList();
                result = await _scheduler.QueueGroupDialogueJobAsync(
                    stringIds,
                    context.CancellationToken
                );
            }
            else
            {
                result = await _scheduler.QueueDialogueJobAsync(
                    AgentIdMapping.GetStringId(request.AgentIdA),
                    AgentIdMapping.GetStringId(request.AgentIdB),
                    context.CancellationToken
                );
            }

            var response = new TriggerDialogueResponse
            {
                TaskId = result.JobId,
                DialogueSummary = result.Summary
            };

            if (result.DialogueLines != null)
            {
                response.DialogueLines.AddRange(result.DialogueLines);
            }

            if (result.StructuredLines != null)
            {
                response.StructuredLines.AddRange(result.StructuredLines);
            }

            if (result.EmotionUpdates != null)
            {
                response.EmotionUpdates.AddRange(result.EmotionUpdates);
            }

            if (result.NextJobs != null)
            {
                var agents = _agentsAccessor();
                foreach (var nj in result.NextJobs)
                {
                    if (agents.TryGetValue(nj.AgentId, out var agent))
                    {
                        response.NextJobs.Add(new JobPayload
                        {
                            NpcId = agent.NumericId,
                            JobId = agent.Status.ActiveJobId,
                            TargetLocation = LocationCoordinateRegistry.CreateLocationInfo(agent.Status.ActiveJobLocation, agent.Status.ActiveJobX, agent.Status.ActiveJobY, agent.Status.ActiveJobZ),
                            Intent = agent.Status.ActiveJobIntent,
                            Priority = 2,
                            Category = JobCategoryMapper.Map(agent.Status.ActiveJobIntent)
                        });
                    }
                }
            }

            if (result.Keywords != null)
            {
                response.Keywords.AddRange(result.Keywords);
            }

            return response;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"대화 트리거 중 서버 오류 발생: {ex.Message}"));
        }
    }

    // [미래 준비용] 특정 에이전트의 상세 상태 및 기억(Memories) 목록을 조회합니다.
    // 향후 유니티 ◄► C++ 간의 TCP 브릿지 패킷(예: CS_INSPECT_NPC_MEMORIES) 추가 시 활성화하여 활용할 예정입니다.
    public override Task<GetAgentStatusResponse> GetAgentStatus(GetAgentStatusRequest request, ServerCallContext context)
    {
        var agents = _agentsAccessor();
        var agentIdStr = AgentIdMapping.GetStringId(request.AgentId);
        if (!agents.TryGetValue(agentIdStr, out var agent))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"ID가 '{request.AgentId}'인 에이전트를 찾을 수 없습니다."));
        }

        var response = new GetAgentStatusResponse
        {
            Name = agent.Persona.Name,
            Location = LocationCoordinateRegistry.CreateLocationInfo(agent.Status.CurrentLocation, agent.Status.X, agent.Status.Y, agent.Status.Z),
            Emotion = agent.Status.Emotion,
            Activity = agent.Status.Activity
        };

        var recentMemories = agent.MemoryBox.Beliefs.Values
            .OrderByDescending(b => b.AcquiredAt)
            .Select(b => $"[{b.AcquiredAt:yyyy-MM-dd HH:mm:ss}] {b.Type}: {b.Content}");
        response.Memories.AddRange(recentMemories);

        return Task.FromResult(response);
    }

    public override Task<InjectBeliefResponse> InjectBelief(InjectBeliefRequest request, ServerCallContext context)
    {
        var agents = _agentsAccessor();
        var targetAgentIdStr = AgentIdMapping.GetStringId(request.TargetAgentId);
        if (!agents.TryGetValue(targetAgentIdStr, out var targetAgent))
        {
            return Task.FromResult(new InjectBeliefResponse
            {
                Success = false,
                Message = $"대상 에이전트 '{request.TargetAgentId}'를 찾을 수 없습니다."
            });
        }

        var subjectIdStr = AgentIdMapping.GetStringId(request.SubjectId);

        var beliefType = request.BeliefType switch
        {
            ProtoBeliefType.BeliefTypeCore => BeliefType.Core,
            ProtoBeliefType.BeliefTypeWitnessed => BeliefType.Witnessed,
            ProtoBeliefType.BeliefTypeHeard => BeliefType.Heard,
            ProtoBeliefType.BeliefTypeOverheard => BeliefType.Overheard,
            _ => BeliefType.Witnessed
        };

        var sourceAgentIdStr = request.SourceAgentId != 0 
            ? AgentIdMapping.GetStringId(request.SourceAgentId) 
            : "ExternalGrpc";

        var belief = new Belief
        {
            BeliefId = $"belief_{subjectIdStr}_{Guid.NewGuid().ToString().Substring(0, 5)}",
            SubjectId = subjectIdStr,
            Content = request.Content,
            Type = beliefType,
            Confidence = 0.8,
            Salience = 1.0,
            EmotionalCharge = 0.5,
            SourceAgentId = sourceAgentIdStr,
            AcquiredAt = DateTime.UtcNow
        };

        targetAgent.MemoryBox.AddOrUpdateBelief(belief);

        return Task.FromResult(new InjectBeliefResponse
        {
            Success = true,
            Message = $"에이전트 '{targetAgent.Persona.Name}'에게 믿음(소문) 주입 성공."
        });
    }


    public override Task<BatchUpdateAgentStatusResponse> BatchUpdateAgentStatus(BatchUpdateAgentStatusRequest request, ServerCallContext context)
    {
        var agents = _agentsAccessor();
        int updatedCount = 0;

        foreach (var agentReq in request.Agents)
        {
            var agentIdStr = AgentIdMapping.GetStringId(agentReq.AgentId);
            if (!agents.TryGetValue(agentIdStr, out var agent))
            {
                Console.WriteLine($"[Warning] BatchUpdate: ID가 '{agentReq.AgentId}'인 에이전트를 찾을 수 없어 건너뜁니다.");
                continue;
            }

            var oldLocation = agent.Status.CurrentLocation;
            var oldX = agent.Status.X;
            var oldY = agent.Status.Y;
            var oldZ = agent.Status.Z;

            // 스레드 세이프하게 상태 갱신
            if (agentReq.Location != null && !string.IsNullOrWhiteSpace(agentReq.Location.Name))
            {
                agent.Status.CurrentLocation = agentReq.Location.Name;
                if (agentReq.Location.Position != null)
                {
                    agent.Status.X = agentReq.Location.Position.X;
                    agent.Status.Y = agentReq.Location.Position.Y;
                    agent.Status.Z = agentReq.Location.Position.Z;
                }
            }
            if (!string.IsNullOrWhiteSpace(agentReq.Emotion)) agent.Status.Emotion = agentReq.Emotion;
            if (!string.IsNullOrWhiteSpace(agentReq.Activity)) agent.Status.Activity = agentReq.Activity;

            Console.WriteLine($"🔄 [gRPC-Batch] 에이전트 '{agent.Persona.Name}' 상태 업데이트: 위치={agent.Status.CurrentLocation} ({agent.Status.X:0.0}, {agent.Status.Y:0.0}, {agent.Status.Z:0.0}), 감정={agent.Status.Emotion}, 행동={agent.Status.Activity}");



            updatedCount++;
        }

        return Task.FromResult(new BatchUpdateAgentStatusResponse
        {
            UpdatedCount = updatedCount
        });
    }

    public override Task<ProcessWorldTickResponse> ProcessWorldTick(ProcessWorldTickRequest request, ServerCallContext context)
    {
        Console.WriteLine($"⏱️ [gRPC] 월드 틱 진행 통보 수신: 틱 번호 {request.TickNumber}");

        // 플레이어 잉여 세션(타임아웃) 정리 (약 2분)
        _ = _playerDialogueManager.CleanupIdleSessionsAsync(TimeSpan.FromMinutes(2), context.CancellationToken);

        // 🆕 믿음 쇠퇴(Decay) 처리: 전역 루프를 제거하고 글로벌 틱 번호만 갱신 (Lazy Decay)
        _beliefEngine.CurrentTick = request.TickNumber;

        // 🆕 예측형 비동기 큐 및 스케줄 교체 처리
        var activeAgents = _agentsAccessor().Values.Where(a => a.AgentId != "player").ToList();
        foreach (var agent in activeAgents)
        {
            // 1. 만료 틱 도달 시 스케줄 예비 버퍼 스왑 시도
            _dailyPlanService.TrySwapNextSchedule(agent.AgentId, request.TickNumber);

            // 2. 스케줄 잔여 틱 계산 및 예측형 큐 삽입 검사
            int remainingTicks = agent.PlanExpirationTick - request.TickNumber;
            if (remainingTicks <= 4)
            {
                _dailyPlanService.EnqueueReflection(agent.AgentId, remainingTicks);
            }
        }

        var busyAgentIds = _agentsAccessor().Values
            .Where(a => a.Status.IsInConversation || _dailyPlanService.IsAgentBusy(a.AgentId))
            .Select(a => a.NumericId)
            .ToList();

        var response = new ProcessWorldTickResponse
        {
            Success = true,
            Message = $"틱 {request.TickNumber} 처리가 정상적으로 완료되었습니다."
        };
        response.BusyAgentIds.AddRange(busyAgentIds);
        
        var deltas = RelationshipChangeTracker.DrainDeltas();
        response.RelationshipDeltas.AddRange(deltas);

        return Task.FromResult(response);
    }

    public override async Task<StartPlayerDialogueResponse> StartPlayerDialogue(StartPlayerDialogueRequest request, ServerCallContext context)
    {
        try
        {
            var npcIdStr = AgentIdMapping.GetStringId(request.NpcId);
            var result = await _playerDialogueManager.StartDialogueAsync(request.PlayerId, npcIdStr, context.CancellationToken);
            return new StartPlayerDialogueResponse
            {
                Success = result.Success,
                Message = result.Message,
                SessionId = result.SessionId,
                Greeting = result.Greeting
            };
        }
        catch (Exception ex)
        {
            return new StartPlayerDialogueResponse
            {
                Success = false,
                Message = $"플레이어 대화 시작 오류: {ex.Message}",
                SessionId = 0,
                Greeting = string.Empty
            };
        }
    }

    public override async Task<SendPlayerMessageResponse> SendPlayerMessage(SendPlayerMessageRequest request, ServerCallContext context)
    {
        try
        {
            var reply = await _playerDialogueManager.SendMessageAsync(request.SessionId, request.Message, context.CancellationToken);
            return new SendPlayerMessageResponse
            {
                Reply = reply
            };
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"플레이어 메시지 전송 오류: {ex.Message}"));
        }
    }

    public override async Task<EndPlayerDialogueResponse> EndPlayerDialogue(EndPlayerDialogueRequest request, ServerCallContext context)
    {
        try
        {
            var result = await _playerDialogueManager.EndDialogueAsync(request.SessionId, context.CancellationToken);
            return new EndPlayerDialogueResponse
            {
                Success = result.Success,
                Summary = result.Summary
            };
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"플레이어 대화 종료 오류: {ex.Message}"));
        }
    }

    public override Task<GetWorldBootstrapResponse> GetWorldBootstrap(GetWorldBootstrapRequest request, ServerCallContext context)
    {
        Console.WriteLine("📥 [gRPC] 월드 부트스트랩 데이터 요청 수신");

        var response = new GetWorldBootstrapResponse();
        var agents = _agentsAccessor();

        foreach (var kv in agents)
        {
            var initialLoc = kv.Value.Status.CurrentLocation;
            var (initX, initY, initZ) = LocationCoordinateRegistry.GetCoordinates(initialLoc);
            kv.Value.Status.X = initX;
            kv.Value.Status.Y = initY;
            kv.Value.Status.Z = initZ;

            var agentState = new InitialAgentState
            {
                AgentId = kv.Value.NumericId,
                Name = kv.Value.Persona.Name ?? string.Empty,
                Location = LocationCoordinateRegistry.CreateLocationInfo(initialLoc, initX, initY, initZ),
                Emotion = kv.Value.Status.Emotion ?? "평온",
                Activity = kv.Value.Status.Activity ?? "대기",
                Extroversion = (float)kv.Value.Persona.Extroversion,
                StringId = kv.Value.AgentId ?? string.Empty
            };

            foreach (var relKv in kv.Value.RelationshipMap)
            {
                agentState.Relationships.Add(new RelationshipSnapshot
                {
                    TargetAgentId = AgentIdMapping.GetNumericId(relKv.Key),
                    Liking = relKv.Value.Liking,
                    Trust = relKv.Value.Trust
                });
            }

            response.Agents.Add(agentState);
        }

        // locations를 LocationCoordinateRegistry에서 동적으로 조회
        var locations = LocationCoordinateRegistry.GetAllSemanticNames();
        response.Locations.AddRange(locations.Select(LocationCoordinateRegistry.CreateLocationInfo));

        // 🆕 동적 가구(사물) 배치 정보 수집 및 전송
        var locationConfigs = LocationCoordinateRegistry.GetConfigs();
        foreach (var locConfig in locationConfigs)
        {
            if (locConfig.Furniture == null) continue;
            foreach (var furnConfig in locConfig.Furniture)
            {
                // 부모 거점의 절대 좌표 + 오프셋 = 가구의 절대 좌표
                float absoluteX = locConfig.Coordinates.X + furnConfig.Offset.X;
                float absoluteY = locConfig.Coordinates.Y + furnConfig.Offset.Y;
                float absoluteZ = locConfig.Coordinates.Z + furnConfig.Offset.Z;

                var protoType = furnConfig.Type.ToLower() switch
                {
                    "sit" => ProtoAffordanceType.AffordanceSit,
                    "sleep" => ProtoAffordanceType.AffordanceSleep,
                    "eat" => ProtoAffordanceType.AffordanceEat,
                    "drink" => ProtoAffordanceType.AffordanceDrink,
                    "pray" => ProtoAffordanceType.AffordancePray,
                    _ => ProtoAffordanceType.AffordanceUnspecified
                };

                response.Furniture.Add(new FurnitureInfo
                {
                    Name = furnConfig.Name,
                    Type = protoType,
                    ParentLocation = locConfig.SemanticName,
                    Position = new Vector3 { X = absoluteX, Y = absoluteY, Z = absoluteZ },
                    IsTemporary = false
                });
            }
        }

        return Task.FromResult(response);
    }

    private static long _globalJobIdSequence = 1000;
    public static ulong GenerateNextJobId() => (ulong)System.Threading.Interlocked.Increment(ref _globalJobIdSequence);

    public override Task<GetPendingJobsResponse> GetPendingJobs(GetPendingJobsRequest request, ServerCallContext context)
    {
        var response = new GetPendingJobsResponse();
        var agents = _agentsAccessor();
        int currentHour = request.CurrentTick % 24;

        foreach (var kvp in agents)
        {
            var agent = kvp.Value;
            if (agent.AgentId == "player") continue;

            // 1. 이미 활성화된 Job이 있으면 해당 Job 정보를 보냄
            if (agent.Status.HasActiveJob)
            {
                response.Jobs.Add(new JobPayload
                {
                    NpcId = agent.NumericId,
                    JobId = agent.Status.ActiveJobId,
                    TargetLocation = LocationCoordinateRegistry.CreateLocationInfo(agent.Status.ActiveJobLocation, agent.Status.ActiveJobX, agent.Status.ActiveJobY, agent.Status.ActiveJobZ),
                    Intent = agent.Status.ActiveJobIntent,
                    Priority = 1,
                    Category = JobCategoryMapper.Map(agent.Status.ActiveJobIntent)
                });
                continue;
            }

            // 2. 이미 활성화된 Job이 없고, 현재 시간에 완료한 작업이 없다면 새 Job 생성
            if (agent.Status.ActiveJobIntent == "생체 욕구 충족 중")
            {
                continue;
            }

            if (agent.Status.LastCompletedHour != currentHour)
            {
                var scheduleItems = _dailyPlanService.GetScheduleForAgent(agent.AgentId);
                var item = scheduleItems.FirstOrDefault(i => i.StartHour <= currentHour && currentHour <= i.EndHour);
                if (item != null && item.Activity != "대기")
                {
                    ulong newJobId = GenerateNextJobId();
                    var (targetX, targetY, targetZ) = LocationCoordinateRegistry.GetTargetCoordinate(agent.Status.CurrentLocation, item.TargetLocation);
                    agent.Status.ActiveJobId = newJobId;
                    agent.Status.ActiveJobLocation = item.TargetLocation;
                    agent.Status.ActiveJobX = targetX;
                    agent.Status.ActiveJobY = targetY;
                    agent.Status.ActiveJobZ = targetZ;
                    agent.Status.ActiveJobIntent = item.Activity;

                    response.Jobs.Add(new JobPayload
                    {
                        NpcId = agent.NumericId,
                        JobId = newJobId,
                        TargetLocation = LocationCoordinateRegistry.CreateLocationInfo(item.TargetLocation, targetX, targetY, targetZ),
                        Intent = item.Activity,
                        Priority = 1,
                        Category = JobCategoryMapper.Map(item.Activity)
                    });

                    Console.WriteLine($"💼 [JobGiver] NPC '{agent.Persona.Name}'에게 새 Job {newJobId} 발급: 위치={item.TargetLocation} ({targetX:0.0}, {targetY:0.0}, {targetZ:0.0}), 행동={item.Activity}");
                }
            }
        }

        return Task.FromResult(response);
    }

    public override async Task<ReportJobStatusResponse> ReportJobStatus(ReportJobStatusRequest request, ServerCallContext context)
    {
        var response = new ReportJobStatusResponse { Success = true };
        var agents = _agentsAccessor();
        var agentIdStr = AgentIdMapping.GetStringId(request.NpcId);

        if (!agents.TryGetValue(agentIdStr, out var agent))
        {
            response.Success = false;
            response.Message = $"에이전트 {request.NpcId}를 찾을 수 없습니다.";
            return response;
        }

        int currentHour = request.CurrentTick % 24;

        if (request.Status == ReportJobStatusRequest.Types.JobStatus.Completed)
        {
            Console.WriteLine($"✅ [JobGiver] NPC '{agent.Persona.Name}'가 Job {request.JobId}를 완료했습니다. (상세: {request.DetailedContext})");

            string completedLocation = agent.Status.ActiveJobLocation;
            string completedIntent = agent.Status.ActiveJobIntent;

            agent.Status.ActiveJobId = 0;
            agent.Status.ActiveJobLocation = string.Empty;
            agent.Status.ActiveJobX = 0f;
            agent.Status.ActiveJobY = 0f;
            agent.Status.ActiveJobZ = 0f;
            agent.Status.ActiveJobIntent = string.Empty;

            if (request.DetailedContext != null && request.DetailedContext.Contains("survival"))
            {
                agent.Status.LastCompletedHour = -1; // 생체 욕구 해결 후 즉시 새 스케줄 발급 허용
            }
            else
            {
                agent.Status.LastCompletedHour = currentHour;
            }

            // 원정 이동 완료 시 조기 취소 및 즉시 재성찰
            if (!string.IsNullOrEmpty(completedIntent) && completedIntent.Contains("이동") && !string.IsNullOrEmpty(completedLocation))
            {
                Console.WriteLine($"🏁 [Arrival] NPC '{agent.Persona.Name}'가 목적지 '{completedLocation}'에 무사히 도착했습니다! 남은 원정 스케줄을 취소하고 현지에서의 신규 일정을 수립합니다.");

                // 1. 남은 오늘 스케줄 단축
                if (agent.CurrentSchedule != null)
                {
                    var updatedSchedule = new List<DailyScheduleItem>();
                    foreach (var item in agent.CurrentSchedule)
                    {
                        if (item.StartHour <= currentHour && currentHour <= item.EndHour)
                        {
                            updatedSchedule.Add(new DailyScheduleItem
                            {
                                StartHour = item.StartHour,
                                EndHour = currentHour,
                                TargetLocation = item.TargetLocation,
                                Activity = item.Activity
                            });
                        }
                        else if (item.EndHour < currentHour)
                        {
                            updatedSchedule.Add(item);
                        }
                    }
                    agent.CurrentSchedule = updatedSchedule;
                }

                // 2. 동적 재성찰 호출 (동기 대기)
                try
                {
                    string reason = "목적지 도착 완료";
                    string contextDetail = $"방금 목적지 [{completedLocation}]에 무사히 도착했습니다. 오늘 남은 시간(현재 {currentHour}시) 동안 이 근방에서 새로 수행할 행동을 계획해 주세요.";
                    
                    var newJobPayload = await TriggerDynamicReflectionAsync(agent, reason, contextDetail, request.CurrentTick);
                    if (newJobPayload != null)
                    {
                        response.NewJob = newJobPayload;
                        response.Message = $"목적지 도착 완료에 따라 새 Job {newJobPayload.JobId}가 즉시 발급되었습니다.";

                        // 3. 오늘 남은 시간(currentHour + 1 ~ 23) 동안의 스케줄 항목 추가
                        int nextHour = currentHour + 1;
                        if (nextHour < 24 && agent.CurrentSchedule != null)
                        {
                            agent.CurrentSchedule.Add(new DailyScheduleItem
                            {
                                StartHour = nextHour,
                                EndHour = 23,
                                TargetLocation = newJobPayload.TargetLocation.Name,
                                Activity = newJobPayload.Intent
                            });
                        }

                        // LiteDB 저장
                        _persistenceService.UpsertAgent(agent);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] 도착 재성찰 오류: {ex.Message}");
                }
            }
        }
        else if (request.Status == ReportJobStatusRequest.Types.JobStatus.Failed)
        {
            Console.WriteLine($"❌ [JobGiver] NPC '{agent.Persona.Name}'가 Job {request.JobId} 수행에 실패했습니다. 사유코드: {request.ReasonCode}, 상세: {request.DetailedContext}");
            agent.Status.ActiveJobId = 0;
            agent.Status.ActiveJobLocation = string.Empty;
            agent.Status.ActiveJobX = 0f;
            agent.Status.ActiveJobY = 0f;
            agent.Status.ActiveJobZ = 0f;
            agent.Status.ActiveJobIntent = string.Empty;
        }
        else if (request.Status == ReportJobStatusRequest.Types.JobStatus.Interrupted)
        {
            string reasonStr = request.ReasonCode switch
            {
                InterruptReason.DialogueBusy => "다른 캐릭터가 말을 걸어 대화가 시작됨",
                InterruptReason.PhysicalAttacked => "물리적인 공격을 받음",
                InterruptReason.EnvironmentChange => "주변 환경에 급격한 변화가 발생함",
                _ => "알 수 없는 사유로 계획 중단"
            };

            Console.WriteLine($"⏸️ [JobGiver] NPC '{agent.Persona.Name}'의 Job {request.JobId}가 중단되었습니다. 사유: {reasonStr} (상세: {request.DetailedContext})");

            // C++ 로컬 생체 욕구(Survival)로 인한 중단인 경우, LLM 성찰 없이 C++ 로컬 처리를 대기
            if (request.DetailedContext != null && request.DetailedContext.Contains("survival"))
            {
                Console.WriteLine($"⏸️ [JobGiver] NPC '{agent.Persona.Name}'가 생체 욕구 해결 중이므로 C# 성찰을 생략하고 C++ 로컬 처리를 대기합니다.");
                agent.Status.ActiveJobId = 0;
                agent.Status.ActiveJobLocation = string.Empty;
                agent.Status.ActiveJobX = 0f;
                agent.Status.ActiveJobY = 0f;
                agent.Status.ActiveJobZ = 0f;
                agent.Status.ActiveJobIntent = "생체 욕구 충족 중";
                response.Message = "생체 욕구 충족 중이므로 C# 성찰을 보류합니다.";
                return response;
            }

            if (request.ReasonCode == InterruptReason.DialogueBusy)
            {
                Console.WriteLine($"⏸️ [JobGiver] NPC '{agent.Persona.Name}'가 대화 중이므로 대기합니다. (원래 계획: {agent.Status.ActiveJobIntent})");
                response.Message = "대화 중이므로 성찰을 생략합니다.";
            }
            else
            {
                try
                {
                    var newJobPayload = await TriggerDynamicReflectionAsync(agent, reasonStr, request.DetailedContext ?? string.Empty, request.CurrentTick);
                    if (newJobPayload != null)
                    {
                        response.NewJob = newJobPayload;
                        response.Message = $"중단 후 새로운 돌발 Job {newJobPayload.JobId}가 성공적으로 수립되었습니다.";
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] 돌발 성찰 실패: {ex.Message}");
                    agent.Status.ActiveJobId = 0;
                    agent.Status.ActiveJobLocation = string.Empty;
                    agent.Status.ActiveJobX = 0f;
                    agent.Status.ActiveJobY = 0f;
                    agent.Status.ActiveJobZ = 0f;
                    agent.Status.ActiveJobIntent = string.Empty;
                    response.Message = $"돌발 성찰 실패: {ex.Message}. 기존 Job을 취소합니다.";
                }
            }
        }

        return response;
    }

    private async Task<JobPayload?> TriggerDynamicReflectionAsync(AgentInstance agent, string reason, string interruptContext, int tick)
    {
        Console.WriteLine($"🧠 [Dynamic Reflection] NPC '{agent.Persona.Name}'의 돌발 성찰 실행 중...");

        string locationList = LocationCoordinateRegistry.GetPromptLocationList();

        string prompt = $$"""
<role>NPC [{{agent.Persona.Name}}]의 상황 변화에 따른 행동 재계획(Dynamic Re-planning) 시스템</role>
<task>원래 계획했던 행동을 수행하던 중 예기치 못한 사건으로 인해 행동이 중단되었습니다. NPC의 가치관과 사건 맥락을 고려하여 다음 행동을 결정해 주십시오.</task>

<rules>
1. 원래 하던 행동을 유지할지, 아니면 새로운 장소로 이동해 다른 행동을 할지 결정하십시오.
2. 결과는 반드시 제공된 JSON 스키마를 충실히 준수하는 순수 JSON이어야 합니다.
3. 이동 가능한 장소 목록 중 하나를 정확하게 선택해야 합니다.
</rules>

<context>
[이동 가능한 장소 목록]
{{locationList}}

[NPC 페르소나]
- 이름/직업: {{agent.Persona.Name}} / {{agent.Persona.Job}}
- 핵심 가치관: {{agent.Persona.CoreValues}}

[중단된 기존 계획]
- 목표 장소: {{agent.Status.ActiveJobLocation}}
- 계획했던 행동: {{agent.Status.ActiveJobIntent}}

[발생한 돌발 사건]
- 중단 사유: {{reason}}
- 상세 맥락: {{interruptContext}}
</context>

<output_format>
반드시 아래 JSON 포맷으로만 응답하십시오.
{
  "target_location": "이동할 장소 (예: 술집 (Tavern))",
  "activity": "새로 수행할 구체적인 행동 (예: 구석에서 조용히 술을 마신다)"
}
</output_format>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", new List<Part> { new Part(prompt) }),
            Contents: new List<Content> { new Content("user", new List<Part> { new Part("재계획 수립 시작.") }) },
            GenerationConfig: new GenerationConfig(null, 1000, "application/json", null, new ThinkingConfig(ThinkingLevel.minimal))
        );

        string responseJson = await _apiService.SendMessageAsync(request, ModelTier.FlashLite);
        var replan = LlmJsonParser.DeserializeSafe<DynamicReplanResponse>(responseJson);

        if (replan != null && !string.IsNullOrWhiteSpace(replan.TargetLocation))
        {
            string correctedLocation = MapToValidLocation(replan.TargetLocation);
            ulong newJobId = GenerateNextJobId();
            var (targetX, targetY, targetZ) = LocationCoordinateRegistry.GetCoordinates(correctedLocation);

            agent.Status.ActiveJobId = newJobId;
            agent.Status.ActiveJobLocation = correctedLocation;
            agent.Status.ActiveJobX = targetX;
            agent.Status.ActiveJobY = targetY;
            agent.Status.ActiveJobZ = targetZ;
            agent.Status.ActiveJobIntent = replan.Activity;

            Console.WriteLine($"🧠 [Dynamic Reflection] NPC '{agent.Persona.Name}'의 새로운 행동 결정: 위치={correctedLocation} ({targetX:0.0}, {targetY:0.0}, {targetZ:0.0}), 행동={replan.Activity}");

            return new JobPayload
            {
                NpcId = agent.NumericId,
                JobId = newJobId,
                TargetLocation = LocationCoordinateRegistry.CreateLocationInfo(correctedLocation, targetX, targetY, targetZ),
                Intent = replan.Activity,
                Priority = 2,
                Category = JobCategoryMapper.Map(replan.Activity)
            };
        }

        return null;
    }

    private string MapToValidLocation(string rawLocation)
    {
        return LocationCoordinateRegistry.ParseLocation(rawLocation);
    }
}

public record DynamicReplanResponse(
    [property: JsonPropertyName("target_location")] string TargetLocation,
    [property: JsonPropertyName("activity")] string Activity
);
