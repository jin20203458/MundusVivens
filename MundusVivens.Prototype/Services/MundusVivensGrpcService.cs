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

    public MundusVivensGrpcService(
        InteractionScheduler scheduler,
        Func<ConcurrentDictionary<string, AgentInstance>> agentsAccessor)
    {
        _scheduler = scheduler;
        _agentsAccessor = agentsAccessor;
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

        // 스레드 세이프하게 상태 갱신
        if (!string.IsNullOrWhiteSpace(request.Location)) agent.Status.CurrentLocation = request.Location;
        if (!string.IsNullOrWhiteSpace(request.Emotion)) agent.Status.Emotion = request.Emotion;
        if (!string.IsNullOrWhiteSpace(request.Activity)) agent.Status.Activity = request.Activity;

        Console.WriteLine($"🔄 [gRPC] 에이전트 '{agent.Persona.Name}' 상태 업데이트: 위치={agent.Status.CurrentLocation}, 감정={agent.Status.Emotion}, 행동={agent.Status.Activity}");

        return Task.FromResult(new UpdateAgentStatusResponse
        {
            Success = true,
            Message = $"에이전트 '{agent.Persona.Name}'의 상태가 동기화되었습니다."
        });
    }

    public override Task<ProcessWorldTickResponse> ProcessWorldTick(ProcessWorldTickRequest request, ServerCallContext context)
    {
        Console.WriteLine($"⏱️ [gRPC] 월드 틱 진행 통보 수신: 틱 번호 {request.TickNumber}");
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
}
