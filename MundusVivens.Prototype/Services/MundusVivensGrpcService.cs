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
    private readonly IWorldEventBroadcaster _broadcaster;
    private readonly IPlayerDialogueManager _playerDialogueManager;
    private readonly IDailyPlanService _dailyPlanService; // 🆕 일일 스케줄 및 성찰 서비스 추가
    private readonly IGossipEngine _gossipEngine; // 🆕 소문 엔진 추가
    private readonly IGeminiApiService _apiService;

    public MundusVivensGrpcService(
        InteractionScheduler scheduler,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
        IWorldEventBroadcaster broadcaster,
        IPlayerDialogueManager playerDialogueManager,
        IDailyPlanService dailyPlanService,
        IGossipEngine gossipEngine,
        IGeminiApiService apiService)
    {
        _scheduler = scheduler;
        _agentsAccessor = agentsAccessor;
        _broadcaster = broadcaster;
        _playerDialogueManager = playerDialogueManager;
        _dailyPlanService = dailyPlanService;
        _gossipEngine = gossipEngine;
        _apiService = apiService;
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
                IsQueued = true,
                CompletedImmediately = result.Success,
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
                            TargetLocation = new LocationInfo
                            {
                                Name = agent.Status.ActiveJobLocation,
                                Position = new Vector3 { X = agent.Status.ActiveJobX, Y = agent.Status.ActiveJobY, Z = agent.Status.ActiveJobZ }
                            },
                            Intent = agent.Status.ActiveJobIntent,
                            Priority = 2 // 돌발 동기화 일정이므로 2 우선순위
                        });
                    }
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"대화 트리거 중 서버 오류 발생: {ex.Message}"));
        }
    }

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
            Location = new LocationInfo
            {
                Name = agent.Status.CurrentLocation,
                Position = new Vector3 { X = agent.Status.X, Y = agent.Status.Y, Z = agent.Status.Z }
            },
            Emotion = agent.Status.Emotion,
            Activity = agent.Status.Activity
        };

        var recentMemories = agent.MemoryBox.EpisodicMemories
            .Select(e => $"[{e.Timestamp:yyyy-MM-dd HH:mm:ss}] {e.TargetName}: {e.Summary}");
        response.Memories.AddRange(recentMemories);

        return Task.FromResult(response);
    }

    public override Task<InjectGossipResponse> InjectGossip(InjectGossipRequest request, ServerCallContext context)
    {
        var agents = _agentsAccessor();
        var targetAgentIdStr = AgentIdMapping.GetStringId(request.TargetAgentId);
        if (!agents.TryGetValue(targetAgentIdStr, out var targetAgent))
        {
            return Task.FromResult(new InjectGossipResponse
            {
                Success = false,
                Message = $"대상 에이전트 '{request.TargetAgentId}'를 찾을 수 없습니다."
            });
        }

        var subjectIdStr = AgentIdMapping.GetStringId(request.SubjectId);
        var gossip = new GossipItem
        {
            GossipId = $"gossip_{subjectIdStr}_{Guid.NewGuid().ToString().Substring(0, 5)}",
            Subject = subjectIdStr,
            Content = request.Content,
            SourceAgentId = "ExternalGrpc",
            BaseCredibility = 80,
            MutationCount = 0
        };

        targetAgent.KnownGossips[gossip.GossipId] = new KnownGossip
        {
            Gossip = gossip,
            SubjectiveBelief = 0.8,
            HasSharedWithOthers = false
        };

        return Task.FromResult(new InjectGossipResponse
        {
            Success = true,
            Message = $"에이전트 '{targetAgent.Persona.Name}'에게 소문 주입 성공."
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

            // 위치가 변경되었을 경우 이동 이벤트 브로드캐스트
            if (agentReq.Location != null && !string.IsNullOrWhiteSpace(agentReq.Location.Name) && oldLocation != agentReq.Location.Name)
            {
                var moveEvent = new WorldEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Movement = new MovementEvent
                    {
                        AgentId = agentReq.AgentId,
                        FromLocation = new LocationInfo
                        {
                            Name = oldLocation,
                            Position = new Vector3 { X = oldX, Y = oldY, Z = oldZ }
                        },
                        ToLocation = agentReq.Location
                    }
                };
                _ = _broadcaster.BroadcastAsync(moveEvent);
            }

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

        // 틱 이벤트 브로드캐스트
        var tickEvent = new WorldEvent
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Tick = new TickEvent
            {
                TickNumber = request.TickNumber
            }
        };
        _ = _broadcaster.BroadcastAsync(tickEvent);

        // 플레이어 잉여 세션(타임아웃) 정리 (약 2분)
        _ = _playerDialogueManager.CleanupIdleSessionsAsync(TimeSpan.FromMinutes(2), context.CancellationToken);

        // 🆕 소문 쇠퇴(Decay) 처리: 전역 루프를 제거하고 글로벌 틱 번호만 갱신 (Lazy Decay)
        _gossipEngine.CurrentTick = request.TickNumber;

        // 🆕 23틱 (자정 직전) 검출 시 백그라운드로 성찰 및 스케줄링 태스크 실행
        if (request.TickNumber % 24 == 23)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _dailyPlanService.PerformReflectionAndGenerateSchedulesAsync(request.TickNumber);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] 백그라운드 성찰/스케줄 수립 실패: {ex.Message}");
                }
            });
        }

        var busyAgentIds = _agentsAccessor().Values
            .Where(a => a.Status.IsInConversation)
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


    public override Task SubscribeWorldEvents(SubscribeRequest request, IServerStreamWriter<WorldEvent> responseStream, ServerCallContext context)
    {
        return _broadcaster.SubscribeAsync(request.SubscriberId, responseStream, context.CancellationToken);
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
                Name = kv.Value.Persona.Name,
                Location = new LocationInfo
                {
                    Name = initialLoc,
                    Position = new Vector3 { X = initX, Y = initY, Z = initZ }
                },
                Emotion = kv.Value.Status.Emotion,
                Activity = kv.Value.Status.Activity,
                Extroversion = (float)kv.Value.Persona.Extroversion
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

        // 인메모리 에이전트의 현재 고유 위치들을 추출하여 반환
        var locations = agents.Values.Select(a => a.Status.CurrentLocation)
                                     .Where(l => !string.IsNullOrEmpty(l) && l != "Unknown")
                                     .Distinct()
                                     .ToList();

        if (locations.Count == 0)
        {
            locations.Add("영주 저택 (Manor)");
            locations.Add("성당 (Church)");
            locations.Add("경비 초소 (Guard Post)");
            locations.Add("연금술 공방 (Alchemy Lab)");
            locations.Add("마을 광장 (Square)");
            locations.Add("대장간 (Forge)");
            locations.Add("뒷골목 (Back Alley)");
            locations.Add("술집 (Tavern)");
        }

        response.Locations.AddRange(locations.Select(LocationCoordinateRegistry.CreateLocationInfo));

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
                    TargetLocation = new LocationInfo
                    {
                        Name = agent.Status.ActiveJobLocation,
                        Position = new Vector3 { X = agent.Status.ActiveJobX, Y = agent.Status.ActiveJobY, Z = agent.Status.ActiveJobZ }
                    },
                    Intent = agent.Status.ActiveJobIntent,
                    Priority = 1
                });
                continue;
            }

            // 2. 이미 활성화된 Job이 없고, 현재 시간에 완료한 작업이 없다면 새 Job 생성
            if (agent.Status.LastCompletedHour != currentHour)
            {
                var scheduleItems = _dailyPlanService.GetScheduleForAgent(agent.AgentId);
                var item = scheduleItems.FirstOrDefault(i => i.StartHour <= currentHour && currentHour <= i.EndHour);
                if (item != null && item.Activity != "대기")
                {
                    ulong newJobId = GenerateNextJobId();
                    var (targetX, targetY, targetZ) = LocationCoordinateRegistry.GetCoordinates(item.TargetLocation);
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
                        TargetLocation = new LocationInfo
                        {
                            Name = item.TargetLocation,
                            Position = new Vector3 { X = targetX, Y = targetY, Z = targetZ }
                        },
                        Intent = item.Activity,
                        Priority = 1
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
            Console.WriteLine($"✅ [JobGiver] NPC '{agent.Persona.Name}'가 Job {request.JobId}를 완료했습니다.");
            agent.Status.ActiveJobId = 0;
            agent.Status.ActiveJobLocation = string.Empty;
            agent.Status.ActiveJobX = 0f;
            agent.Status.ActiveJobY = 0f;
            agent.Status.ActiveJobZ = 0f;
            agent.Status.ActiveJobIntent = string.Empty;
            agent.Status.LastCompletedHour = currentHour;
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

            if (request.ReasonCode == InterruptReason.DialogueBusy)
            {
                Console.WriteLine($"⏸️ [JobGiver] NPC '{agent.Persona.Name}'가 대화 중이므로 대기합니다. (원래 계획: {agent.Status.ActiveJobIntent})");
                response.Message = "대화 중이므로 성찰을 생략합니다.";
            }
            else
            {
                try
                {
                    var newJobPayload = await TriggerDynamicReflectionAsync(agent, reasonStr, request.DetailedContext, request.CurrentTick);
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
                TargetLocation = new LocationInfo
                {
                    Name = correctedLocation,
                    Position = new Vector3 { X = targetX, Y = targetY, Z = targetZ }
                },
                Intent = replan.Activity,
                Priority = 2
            };
        }

        return null;
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
}

public record DynamicReplanResponse(
    [property: JsonPropertyName("target_location")] string TargetLocation,
    [property: JsonPropertyName("activity")] string Activity
);
