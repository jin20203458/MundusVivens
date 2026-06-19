using Grpc.Core;
using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Protos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public class MundusVivensGrpcService : MundusVivensGrpc.MundusVivensGrpcBase
{
    private readonly InteractionScheduler _scheduler;
    private readonly Func<ConcurrentDictionary<string, AgentInstance>> _agentsAccessor;
    private readonly IWorldEventBroadcaster _broadcaster;
    private readonly IPlayerDialogueManager _playerDialogueManager;

    public MundusVivensGrpcService(
        InteractionScheduler scheduler,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor,
        IWorldEventBroadcaster broadcaster,
        IPlayerDialogueManager playerDialogueManager)
    {
        _scheduler = scheduler;
        _agentsAccessor = agentsAccessor;
        _broadcaster = broadcaster;
        _playerDialogueManager = playerDialogueManager;
    }

    public override async Task<TriggerDialogueResponse> TriggerDialogue(TriggerDialogueRequest request, ServerCallContext context)
    {
        try
        {
            var result = await _scheduler.QueueDialogueJobAsync(
                request.AgentIdA,
                request.AgentIdB,
                request.WaitForCompletion,
                context.CancellationToken
            );

            var response = new TriggerDialogueResponse
            {
                TaskId = result.JobId,
                IsQueued = true,
                CompletedImmediately = request.WaitForCompletion && result.Success,
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
        if (!agents.TryGetValue(request.AgentId, out var agent))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"ID가 '{request.AgentId}'인 에이전트를 찾을 수 없습니다."));
        }

        var response = new GetAgentStatusResponse
        {
            Name = agent.Persona.Name,
            Location = agent.Status.CurrentLocation,
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
        if (!agents.TryGetValue(request.TargetAgentId, out var targetAgent))
        {
            return Task.FromResult(new InjectGossipResponse
            {
                Success = false,
                Message = $"대상 에이전트 '{request.TargetAgentId}'를 찾을 수 없습니다."
            });
        }

        var gossip = new GossipItem
        {
            GossipId = $"gossip_{request.SubjectId}_{Guid.NewGuid().ToString().Substring(0, 5)}",
            Subject = request.SubjectId,
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

    public override Task<UpdateAgentStatusResponse> UpdateAgentStatus(UpdateAgentStatusRequest request, ServerCallContext context)
    {
        var agents = _agentsAccessor();
        if (!agents.TryGetValue(request.AgentId, out var agent))
        {
            return Task.FromResult(new UpdateAgentStatusResponse
            {
                Success = false,
                Message = $"에이전트 '{request.AgentId}'를 찾을 수 없습니다."
            });
        }

        var oldLocation = agent.Status.CurrentLocation;

        // 스레드 세이프하게 상태 갱신
        if (!string.IsNullOrWhiteSpace(request.Location)) agent.Status.CurrentLocation = request.Location;
        if (!string.IsNullOrWhiteSpace(request.Emotion)) agent.Status.Emotion = request.Emotion;
        if (!string.IsNullOrWhiteSpace(request.Activity)) agent.Status.Activity = request.Activity;

        Console.WriteLine($"🔄 [gRPC] 에이전트 '{agent.Persona.Name}' 상태 업데이트: 위치={agent.Status.CurrentLocation}, 감정={agent.Status.Emotion}, 행동={agent.Status.Activity}");

        // 위치가 변경되었을 경우 이동 이벤트 브로드캐스트
        if (!string.IsNullOrWhiteSpace(request.Location) && oldLocation != request.Location)
        {
            var moveEvent = new WorldEvent
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Movement = new MovementEvent
                {
                    AgentId = request.AgentId,
                    FromLocation = oldLocation,
                    ToLocation = request.Location
                }
            };
            _ = _broadcaster.BroadcastAsync(moveEvent);
        }

        return Task.FromResult(new UpdateAgentStatusResponse
        {
            Success = true,
            Message = $"에이전트 '{agent.Persona.Name}'의 상태가 동기화되었습니다."
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

        return Task.FromResult(new ProcessWorldTickResponse
        {
            Success = true,
            Message = $"틱 {request.TickNumber} 처리가 정상적으로 완료되었습니다."
        });
    }

    public override Task<GetDialogueResultResponse> GetDialogueResult(GetDialogueResultRequest request, ServerCallContext context)
    {
        var response = new GetDialogueResultResponse
        {
            TaskId = request.TaskId
        };

        if (_scheduler.TryGetCompletedResult(request.TaskId, out var result) && result != null)
        {
            response.IsCompleted = true;
            if (result.Success)
            {
                response.DialogueSummary = result.Summary;
                if (result.StructuredLines != null)
                {
                    response.Lines.AddRange(result.StructuredLines);
                }
            }
            else
            {
                response.DialogueSummary = $"[Error] {result.ErrorMessage}";
            }
        }
        else
        {
            response.IsCompleted = false;
        }

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
            var result = await _playerDialogueManager.StartDialogueAsync(request.PlayerId, request.NpcId, context.CancellationToken);
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
                SessionId = string.Empty,
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
            response.Agents.Add(new InitialAgentState
            {
                AgentId = kv.Value.AgentId,
                Name = kv.Value.Persona.Name,
                Location = kv.Value.Status.CurrentLocation,
                Emotion = kv.Value.Status.Emotion,
                Activity = kv.Value.Status.Activity
            });
        }

        // 인메모리 에이전트의 현재 고유 위치들을 추출하여 반환
        var locations = agents.Values.Select(a => a.Status.CurrentLocation)
                                     .Where(l => !string.IsNullOrEmpty(l) && l != "Unknown")
                                     .Distinct()
                                     .ToList();

        if (locations.Count == 0)
        {
            locations.Add("성당 (Church)");
            locations.Add("술집 (Tavern)");
            locations.Add("광장 (Square)");
        }

        response.Locations.AddRange(locations);

        return Task.FromResult(response);
    }
}
