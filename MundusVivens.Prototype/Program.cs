using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MundusVivens.Prototype.Helpers;
using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Services;
using MundusVivens.Prototype.Protos;
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
        builder.Services.AddSingleton<IPlayerDialogueManager, PlayerDialogueManager>();
        builder.Services.AddSingleton<IWorldEventBroadcaster, WorldEventBroadcaster>();
        builder.Services.AddSingleton<IDailyPlanService, DailyPlanService>();

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
            var broadcaster = sp.GetRequiredService<IWorldEventBroadcaster>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InteractionScheduler>>();
            return new InteractionScheduler(orchestrator, accessor, broadcaster, logger, maxGlobalConcurrent);
        });
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InteractionScheduler>());

        // gRPC 서비스 등록
        builder.Services.AddGrpc();

        // CORS 서비스 등록 (웹 대시보드의 원활한 접근 허용)
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            });
        });

        var app = builder.Build();

        app.UseCors();

        // 3. gRPC 라우팅 매핑
        app.MapGrpcService<MundusVivensGrpcService>();

        // 초기 기본 스케줄 채우기
        app.Services.GetRequiredService<IDailyPlanService>().InitializeDefaultSchedules();

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
                    result.DialogueLines,
                    result.StructuredLines
                });
            }

            return Results.Accepted($"/api/interaction/result/{result.JobId}", new
            {
                TaskId = result.JobId,
                Status = "Queued",
                Message = result.Summary
            });
        });

        // 완료된 대화 결과 조회 (REST API 방식)
        app.MapGet("/api/interaction/result/{jobId}", (string jobId, InteractionScheduler scheduler) =>
        {
            if (scheduler.TryGetCompletedResult(jobId, out var result) && result != null)
            {
                return Results.Ok(new
                {
                    result.JobId,
                    Status = result.Success ? "Completed" : "Failed",
                    result.Success,
                    result.ErrorMessage,
                    result.Summary,
                    result.DialogueLines,
                    result.StructuredLines
                });
            }

            return Results.Ok(new
            {
                JobId = jobId,
                Status = "Processing",
                Message = "대화가 아직 진행 중이거나 큐에서 대기 중입니다."
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

        // 1. 대시보드 뷰어 서빙
        app.MapGet("/", async () =>
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "index.html");
            if (!File.Exists(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            }

            if (!File.Exists(path))
            {
                return Results.NotFound("Dashboard UI file (wwwroot/index.html) not found. Please verify placement.");
            }

            return Results.Content(await File.ReadAllTextAsync(path, Encoding.UTF8), "text/html", Encoding.UTF8);
        });

        // 2. Server-Sent Events (SSE) 실시간 이벤트 스트림
        app.MapGet("/api/events", async (HttpContext httpContext, IWorldEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";

            var channel = System.Threading.Channels.Channel.CreateUnbounded<WorldEvent>();
            Action<WorldEvent> onEvent = (ev) => channel.Writer.TryWrite(ev);
            broadcaster.OnWorldEvent += onEvent;

            try
            {
                await httpContext.Response.WriteAsync($": welcome\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    var ev = await channel.Reader.ReadAsync(ct);
                    var json = Google.Protobuf.JsonFormatter.Default.Format(ev);
                    await httpContext.Response.WriteAsync($"data: {json}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal disconnect
            }
            finally
            {
                broadcaster.OnWorldEvent -= onEvent;
            }
        });

        // 3. 플레이어 대화 제어 REST API
        app.MapPost("/api/player/dialogue/start", async (StartPlayerDialogueApiRequest req, IPlayerDialogueManager mgr, CancellationToken ct) =>
        {
            try
            {
                var (success, message, sessionId, greeting) = await mgr.StartDialogueAsync(req.PlayerId, req.NpcId, ct);
                return Results.Ok(new
                {
                    Success = success,
                    Message = message,
                    SessionId = sessionId,
                    Greeting = greeting
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        app.MapPost("/api/player/dialogue/send", async (SendPlayerMessageApiRequest req, IPlayerDialogueManager mgr, CancellationToken ct) =>
        {
            try
            {
                var reply = await mgr.SendMessageAsync(req.SessionId, req.Message, ct);
                return Results.Ok(new { Reply = reply });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        app.MapPost("/api/player/dialogue/end", async (EndPlayerDialogueApiRequest req, IPlayerDialogueManager mgr, CancellationToken ct) =>
        {
            try
            {
                var (success, summary) = await mgr.EndDialogueAsync(req.SessionId, ct);
                return Results.Ok(new
                {
                    Success = success,
                    Summary = summary
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        app.Run();
    }

    // REST API 바인딩 모델 정의
    public record InteractionRequest(string AgentIdA, string AgentIdB, bool? Wait);
    public record GossipInjectRequest(string SubjectId, string Content, string TargetAgentId);
    public record StartPlayerDialogueApiRequest(string PlayerId, string NpcId);
    public record SendPlayerMessageApiRequest(string SessionId, string Message);
    public record EndPlayerDialogueApiRequest(string SessionId);

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
                Extroversion = 0.4,
                Faction = "성당 세력"
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
                Extroversion = 0.9,
                Faction = "마을 주민"
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
                Extroversion = 0.5,
                Faction = "자유 용병"
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

        // 4. 플레이어 (Player) - 특수 에이전트 (4-B-4)
        var player = new AgentInstance
        {
            AgentId = "player",
            Persona = new Persona
            {
                Name = "플레이어",
                Job = "여행자",
                ToneStyle = "자유로운 말투",
                Backstory = "가상 세계를 탐험하며 주민들의 비밀을 파헤치는 이방인입니다.",
                CoreValues = "호기심, 진실 규명",
                Extroversion = 0.5,
                Faction = "여행자"
            },
            Status = new AgentStatus
            {
                CurrentLocation = "광장 (Square)",
                Emotion = "평온함",
                Activity = "마을 둘러보기"
            }
        };
        dict[player.AgentId] = player;

        // 초기 관계 세팅
        SetInitialRelationship(eva, "npc_bart", 20, 70);
        SetInitialRelationship(bart, "npc_eva", 25, 75);

        SetInitialRelationship(kyle, "npc_eva", 5, 50);
        SetInitialRelationship(eva, "npc_kyle", 10, 60);

        SetInitialRelationship(bart, "npc_kyle", -15, 30);
        SetInitialRelationship(kyle, "npc_bart", 0, 50);

        // 플레이어에 대한 NPC들의 초기 관계 세팅
        SetInitialRelationship(kyle, "player", 0, 50);
        SetInitialRelationship(eva, "player", 15, 60);
        SetInitialRelationship(bart, "player", -5, 40);

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
