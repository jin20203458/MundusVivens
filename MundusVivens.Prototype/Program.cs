using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MundusVivens.Prototype.Helpers;
using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MundusVivens.Prototype;

public class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=======================================================");
        Console.WriteLine("🌍 Project Mundus Vivens — Phase 3.5 Gossip Improved Server");
        Console.WriteLine("=======================================================\n");

        var builder = WebApplication.CreateBuilder(args);

        // 1. 설정 불러오기
        var config = builder.Configuration;
        var maxGlobalConcurrent = config.GetValue<int>("SimulationConfig:MaxGlobalConcurrent", 2);
        var sessionId = config.GetValue<string>("SimulationConfig:SessionId") ?? DateTime.Now.ToString("yyyyMMdd_HHmmss");

        Console.WriteLine($"[Config] SessionId: {sessionId}");
        Console.WriteLine($"[Config] MaxGlobalConcurrent: {maxGlobalConcurrent}");

        // 2. 의존성 등록
        builder.Services.AddHttpClient();
        
        // 싱글톤 로거 헬퍼 등록
        var tokenLogger = new TokenLogger(sessionId);
        var memoryLogger = new MemoryEventLogger(sessionId);
        builder.Services.AddSingleton(tokenLogger);
        builder.Services.AddSingleton(memoryLogger);

        builder.Services.AddSingleton<IGoogleAuthService, GoogleAuthService>();
        builder.Services.AddSingleton<IGeminiApiService, GeminiApiService>();
        builder.Services.AddSingleton<IGossipEngine, GossipEngine>();
        builder.Services.AddSingleton<IDialogueOrchestrator, DialogueOrchestrator>();

        // 에이전트 인메모리 저장소
        var initialAgents = InitializeAgents();
        SeedGossip(initialAgents);
        builder.Services.AddSingleton<ConcurrentDictionary<string, AgentInstance>>(initialAgents);
        builder.Services.AddSingleton<Func<ConcurrentDictionary<string, AgentInstance>>>(sp => () => sp.GetRequiredService<ConcurrentDictionary<string, AgentInstance>>());

        // 비동기 스케줄러 등록
        builder.Services.AddSingleton<InteractionScheduler>(sp =>
        {
            var orchestrator = sp.GetRequiredService<IDialogueOrchestrator>();
            var accessor = sp.GetRequiredService<Func<ConcurrentDictionary<string, AgentInstance>>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InteractionScheduler>>();
            return new InteractionScheduler(orchestrator, accessor, logger, maxGlobalConcurrent);
        });
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InteractionScheduler>());

        // gRPC 서비스 등록
        builder.Services.AddGrpc();

        var app = builder.Build();

        // 3. gRPC 라우팅 매핑
        app.MapGrpcService<MundusVivensGrpcService>();

        // 4. Minimal APIs (REST API) 라우팅 매핑
        
        // 현재 모든 에이전트 정보 조회
        app.MapGet("/api/agents", (ConcurrentDictionary<string, AgentInstance> agents) =>
        {
            return Results.Ok(agents.Values.Select(a => new
            {
                a.AgentId,
                a.Persona.Name,
                a.Persona.Job,
                a.Status.CurrentLocation,
                a.Status.Emotion,
                a.Status.Activity,
                a.Status.IsInConversation,
                KnownGossips = a.KnownGossips.Values.Select(g => new
                {
                    g.Gossip.GossipId,
                    g.Gossip.Subject,
                    g.Gossip.Content,
                    g.SubjectiveBelief
                }),
                Relationships = a.RelationshipMap.Values.Select(r => new
                {
                    r.TargetAgentId,
                    r.Liking,
                    r.Trust
                })
            }));
        });

        // 특정 에이전트 조회
        app.MapGet("/api/agents/{agentId}", (string agentId, ConcurrentDictionary<string, AgentInstance> agents) =>
        {
            if (!agents.TryGetValue(agentId, out var agent))
            {
                return Results.NotFound(new { Message = $"에이전트 '{agentId}'를 찾을 수 없습니다." });
            }
            return Results.Ok(agent);
        });

        // 에이전트 강제 리셋
        app.MapPost("/api/agents/reset", (ConcurrentDictionary<string, AgentInstance> agents) =>
        {
            var resetData = InitializeAgents();
            SeedGossip(resetData);
            
            agents.Clear();
            foreach (var kv in resetData)
            {
                agents[kv.Key] = kv.Value;
            }
            
            return Results.Ok(new { Message = "시뮬레이션 인메모리 데이터가 초기 상태로 리셋되었습니다." });
        });

        // 인메모리 세션 스냅샷 파일 저장
        app.MapPost("/api/agents/save", (ConcurrentDictionary<string, AgentInstance> agents) =>
        {
            try
            {
                var savePath = Path.Combine(Directory.GetCurrentDirectory(), "SessionDump.json");
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(agents, options);
                File.WriteAllText(savePath, json, Encoding.UTF8);
                return Results.Ok(new { Message = $"현재 인메모리 세션이 성공적으로 저장되었습니다.", Path = savePath });
            }
            catch (Exception ex)
            {
                return Results.InternalServerError(new { Error = ex.Message });
            }
        });

        // 인메모리 세션 스냅샷 파일 로드
        app.MapPost("/api/agents/load", (ConcurrentDictionary<string, AgentInstance> agents) =>
        {
            try
            {
                var savePath = Path.Combine(Directory.GetCurrentDirectory(), "SessionDump.json");
                if (!File.Exists(savePath))
                {
                    return Results.NotFound(new { Message = "저장된 세션 백업 파일(SessionDump.json)을 찾을 수 없습니다." });
                }

                var json = File.ReadAllText(savePath, Encoding.UTF8);
                var loadedAgents = JsonSerializer.Deserialize<ConcurrentDictionary<string, AgentInstance>>(json);
                if (loadedAgents != null)
                {
                    agents.Clear();
                    foreach (var kv in loadedAgents)
                    {
                        agents[kv.Key] = kv.Value;
                    }
                    return Results.Ok(new { Message = "세션 백업 파일로부터 인메모리 데이터를 성공적으로 로드했습니다." });
                }
                return Results.BadRequest(new { Message = "데이터 역직렬화에 실패했습니다." });
            }
            catch (Exception ex)
            {
                return Results.InternalServerError(new { Error = ex.Message });
            }
        });

        // 대화 실행 (REST API 방식)
        app.MapPost("/api/interaction/nearby", async (
            InteractionRequest request,
            InteractionScheduler scheduler,
            ConcurrentDictionary<string, AgentInstance> agents,
            CancellationToken ct) =>
        {
            bool wait = request.Wait ?? true;
            
            var result = await scheduler.QueueDialogueJobAsync(request.AgentIdA, request.AgentIdB, wait, ct);
            
            if (!result.Success)
            {
                return Results.BadRequest(new { Error = result.ErrorMessage });
            }

            if (wait)
            {
                return Results.Ok(new
                {
                    result.JobId,
                    Status = "Completed",
                    result.Summary,
                    result.DialogueLines
                });
            }

            return Results.Accepted($"/api/interaction/active", new
            {
                TaskId = result.JobId,
                Status = "Queued",
                Message = result.Summary
            });
        });

        // 현재 대기 중이거나 진행 중인 작업 조회
        app.MapGet("/api/interaction/active", (InteractionScheduler scheduler) =>
        {
            return Results.Ok(scheduler.GetActiveAndPendingJobs());
        });

        // 토큰 실시간 통계 조회
        app.MapGet("/api/logs/tokens", (TokenLogger logger) =>
        {
            return Results.Ok(new
            {
                logger.TotalPromptTokens,
                logger.TotalCompletionTokens,
                logger.TotalThinkingTokens,
                logger.TotalTokens,
                logger.ApproximateCostUsd
            });
        });

        // 기억 전문 로그 읽기
        app.MapGet("/api/logs/memory", async (MemoryEventLogger logger) =>
        {
            var content = await logger.ReadAllLogsAsync();
            return Results.Text(content, "text/plain", Encoding.UTF8);
        });

        // 소문 강제 주입
        app.MapPost("/api/gossip/inject", (
            GossipInjectRequest request,
            ConcurrentDictionary<string, AgentInstance> agents) =>
        {
            if (!agents.TryGetValue(request.TargetAgentId, out var targetAgent))
            {
                return Results.NotFound(new { Error = $"대상 에이전트 '{request.TargetAgentId}'를 찾을 수 없습니다." });
            }

            var gossip = new GossipItem
            {
                GossipId = $"gossip_{request.SubjectId}_{Guid.NewGuid().ToString().Substring(0, 5)}",
                Subject = request.SubjectId,
                Content = request.Content,
                SourceAgentId = "ExternalREST",
                BaseCredibility = 80,
                MutationCount = 0
            };

            targetAgent.KnownGossips[gossip.GossipId] = new KnownGossip
            {
                Gossip = gossip,
                SubjectiveBelief = 0.8,
                HasSharedWithOthers = false
            };

            return Results.Ok(new { Message = $"에이전트 '{targetAgent.Persona.Name}'에게 소문 주입 완료" });
        });

        app.Run();
    }

    // REST API 바인딩 모델 정의
    public record InteractionRequest(string AgentIdA, string AgentIdB, bool? Wait);
    public record GossipInjectRequest(string SubjectId, string Content, string TargetAgentId);

    private static ConcurrentDictionary<string, AgentInstance> InitializeAgents()
    {
        var dict = new ConcurrentDictionary<string, AgentInstance>();

        // 1. 카일 (Kyle) - 성직자
        var kyle = new AgentInstance
        {
            AgentId = "npc_kyle",
            Persona = new Persona
            {
                Name = "카일",
                Job = "성직자",
                ToneStyle = "정중하고 신중하며 부드러운 경어체를 구사함",
                Backstory = "어린 시절부터 성당에서 교육받아 깊은 신앙심을 가진 젊은 부사제입니다. 하지만 성당 내부의 야망을 숨기고 있습니다.",
                CoreValues = "교회의 권위, 평화, 기도",
                Extroversion = 0.4
            },
            Status = new AgentStatus
            {
                CurrentLocation = "성당 (Church)",
                Emotion = "평온함",
                Activity = "예배 조율"
            }
        };
        kyle.MemoryBox.CoreMemories.Add(new CoreFact("성당의 성수를 몰래 처분했다는 은밀한 비밀을 품고 있음", 9));
        dict[kyle.AgentId] = kyle;

        // 2. 에바 (Eva) - 바텐더
        var eva = new AgentInstance
        {
            AgentId = "npc_eva",
            Persona = new Persona
            {
                Name = "에바",
                Job = "술집 바텐더",
                ToneStyle = "쾌활하고 친근하며 반말과 친근한 경어를 혼용하여 수다스러움",
                Backstory = "성당 옆 술집을 수년째 구동하고 있는 여성으로, 마을의 모든 소문이 그녀의 귀를 거쳐 갑니다. 이야기하는 것을 인생의 기쁨으로 여깁니다.",
                CoreValues = "유쾌한 소통, 마을 사람들과의 친화",
                Extroversion = 0.9
            },
            Status = new AgentStatus
            {
                CurrentLocation = "술집 (Tavern)",
                Emotion = "유쾌함",
                Activity = "맥주컵 닦기"
            }
        };
        dict[eva.AgentId] = eva;

        // 3. 바르트 (Bart) - 노용병
        var bart = new AgentInstance
        {
            AgentId = "npc_bart",
            Persona = new Persona
            {
                Name = "바르트",
                Job = "노용병",
                ToneStyle = "거칠고 퉁명스러우며 직설적이고 반말 위주의 말투",
                Backstory = "왕년의 전장을 누비던 은퇴한 늙은 전사입니다. 교회의 위선을 혐오하며, 종교인들을 믿지 않습니다.",
                CoreValues = "명예, 자유, 의심",
                Extroversion = 0.5
            },
            Status = new AgentStatus
            {
                CurrentLocation = "술집 (Tavern)",
                Emotion = "무덤덤함",
                Activity = "술 마시는 중"
            }
        };
        bart.MemoryBox.CoreMemories.Add(new CoreFact("과거 전쟁터에서 사제들의 배신으로 전우를 잃어 종교인을 극도로 불신함", 10));
        dict[bart.AgentId] = bart;

        // 초기 관계 세팅
        SetInitialRelationship(eva, "npc_bart", 20, 70);
        SetInitialRelationship(bart, "npc_eva", 25, 75);

        SetInitialRelationship(kyle, "npc_eva", 5, 50);
        SetInitialRelationship(eva, "npc_kyle", 10, 60);

        SetInitialRelationship(bart, "npc_kyle", -15, 30);
        SetInitialRelationship(kyle, "npc_bart", 0, 50);

        return dict;
    }

    private static void SetInitialRelationship(AgentInstance owner, string targetId, int liking, int trust)
    {
        owner.RelationshipMap[targetId] = new Relationship
        {
            TargetAgentId = targetId,
            Liking = liking,
            Trust = trust
        };
    }

    private static void SeedGossip(ConcurrentDictionary<string, AgentInstance> agents)
    {
        var gossip = new GossipItem
        {
            GossipId = "gossip_holy_water_theft",
            Subject = "npc_kyle",
            Content = "카일이 밤중에 성당 창고에서 귀중한 의식용 성수를 훔쳐 빼돌렸다",
            SourceAgentId = "Player",
            BaseCredibility = 90,
            MutationCount = 0
        };

        agents["npc_eva"].KnownGossips[gossip.GossipId] = new KnownGossip
        {
            Gossip = gossip,
            SubjectiveBelief = 0.95,
            HasSharedWithOthers = false
        };

        Console.WriteLine($"📢 [System Info] 에바가 '{gossip.Subject}'에 관한 새로운 비밀 소문을 알게 되었습니다.");
        Console.WriteLine($"   => 내용: \"{gossip.Content}\" (신뢰도: {gossip.BaseCredibility}%)\n");
    }
}
