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

        #if DEBUG
        // Initialize diagnostic logging pipeline
        AiAgent.Diagnostics.AiDebugLogger.BasePath = AppDomain.CurrentDomain.BaseDirectory;
        AiAgent.Diagnostics.AiDiagnosticChannels.Start();
        AiAgent.Diagnostics.AiDiagnosticObserver.Register();
        #endif

        var builder = WebApplication.CreateBuilder(args);

        // 1. 설정 불러오기
        var config = builder.Configuration;
        var maxGlobalConcurrent = config.GetValue<int>("SimulationConfig:MaxGlobalConcurrent", 10);
        var sessionId = config.GetValue<string>("SimulationConfig:SessionId") ?? DateTime.Now.ToString("yyyyMMdd_HHmmss");

        Console.WriteLine($"[Config] SessionId: {sessionId}");
        Console.WriteLine($"[Config] MaxGlobalConcurrent: {maxGlobalConcurrent}");

        // 2. 의존성 등록
        builder.Services.AddHttpClient();
        
        // 싱글톤 로거 헬퍼 등록
        var tokenLogger = new TokenLogger(sessionId);
        var memoryLogger = new MemoryEventLogger(sessionId);
        var llmResponseLogger = new LlmResponseLogger(sessionId);
        builder.Services.AddSingleton(tokenLogger);
        builder.Services.AddSingleton(memoryLogger);
        builder.Services.AddSingleton(llmResponseLogger);

        builder.Services.AddSingleton<IGoogleAuthService, GoogleAuthService>();
        builder.Services.AddSingleton<IGeminiApiService, GeminiApiService>();
        builder.Services.AddSingleton<IEmbeddingCache, EmbeddingCache>();
        builder.Services.AddSingleton<IBeliefEngine, BeliefEngine>();
        builder.Services.AddSingleton<IWorldContextService, WorldContextService>();
        builder.Services.AddSingleton<IDialogueOrchestrator, DialogueOrchestrator>();
        builder.Services.AddSingleton<IPlayerDialogueManager, PlayerDialogueManager>();
        builder.Services.AddSingleton<IDailyPlanService, DailyPlanService>();

        // 데이터 영속성 관련 서비스 직접 인스턴스 생성 (BuildServiceProvider 경고 제거)
        var persistenceService = new PersistenceService();
        // [Smell #5 해결] 시뮬레이션 설정 파일 로드
        var settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "shared_simulation_settings.json");
        if (!File.Exists(settingsPath))
        {
            settingsPath = Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "", "shared_simulation_settings.json");
        }
        LocationCoordinateRegistry.LoadSettings(settingsPath);

        var staticDataLoader = new StaticDataLoader();
        builder.Services.AddSingleton<IPersistenceService>(persistenceService);
        builder.Services.AddSingleton<StaticDataLoader>(staticDataLoader);

        // 에이전트 인메모리 저장소 초기 로드 및 연동
        var forceResetDb = config.GetValue<bool>("SimulationConfig:ForceResetDatabase", false);
        var agentsFromDb = persistenceService.LoadAllAgents();

        // 통합 신념체계 개편에 따라 DB가 비었거나 레거시 스키마(Beliefs가 비어있음)인 경우 혹은 ForceResetDatabase가 활성화된 경우 강제 초기화
        if (forceResetDb || agentsFromDb.IsEmpty || agentsFromDb.Values.All(a => a.MemoryBox.Beliefs.IsEmpty))
        {
            Console.WriteLine($"[System] Resetting database with initial configurations from world_config.json (ForceResetDatabase: {forceResetDb})...");
            var initialAgents = staticDataLoader.LoadInitialAgents();
            persistenceService.ResetDatabase(initialAgents.Values);
            agentsFromDb = persistenceService.LoadAllAgents();
        }
        else
        {
            // DB를 초기화하지 않는 경우에도 world_config.json에서 지역 정보를 읽어 LocationCoordinateRegistry를 초기화해야 함
            staticDataLoader.LoadInitialAgents();
        }

        builder.Services.AddSingleton<ConcurrentDictionary<string, AgentInstance>>(agentsFromDb);
        builder.Services.AddSingleton<Func<ConcurrentDictionary<string, AgentInstance>>>(sp => () => sp.GetRequiredService<ConcurrentDictionary<string, AgentInstance>>());

        builder.Services.AddSingleton<InteractionScheduler>(sp =>
        {
            var orchestrator = sp.GetRequiredService<IDialogueOrchestrator>();
            var accessor = sp.GetRequiredService<Func<ConcurrentDictionary<string, AgentInstance>>>();
            var persistence = sp.GetRequiredService<IPersistenceService>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InteractionScheduler>>();
            return new InteractionScheduler(orchestrator, accessor, persistence, logger, maxGlobalConcurrent);
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

        #if DEBUG
        // Gracefully stop the diagnostic channels on app shutdown
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            AiAgent.Diagnostics.AiDiagnosticObserver.Unregister();
            AiAgent.Diagnostics.AiDiagnosticChannels.StopAsync().GetAwaiter().GetResult();
        });
        #endif

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
                Beliefs = a.MemoryBox.Beliefs.Values.Select(b => new
                {
                    b.BeliefId,
                    b.SubjectId,
                    b.Content,
                    b.Type,
                    b.Confidence,
                    b.Salience,
                    b.EmotionalCharge
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
        app.MapPost("/api/agents/reset", (
            ConcurrentDictionary<string, AgentInstance> agents,
            StaticDataLoader dataLoader,
            IPersistenceService persistence) =>
        {
            try
            {
                var resetData = dataLoader.LoadInitialAgents();
                persistence.ResetDatabase(resetData.Values);
                
                agents.Clear();
                foreach (var kv in resetData)
                {
                    agents[kv.Key] = kv.Value;
                }
                
                return Results.Ok(new { Message = "시뮬레이션 인메모리 및 DB 데이터가 초기 정적 JSON 상태로 리셋되었습니다." });
            }
            catch (Exception ex)
            {
                return Results.InternalServerError(new { Error = ex.Message });
            }
        });

        // 인메모리 세션 스냅샷 파일 저장
        app.MapPost("/api/agents/save", (
            ConcurrentDictionary<string, AgentInstance> agents,
            IPersistenceService persistence) =>
        {
            try
            {
                foreach (var agent in agents.Values)
                {
                    persistence.UpsertAgent(agent);
                }
                return Results.Ok(new { Message = "현재 인메모리 데이터의 스냅샷이 LiteDB에 성공적으로 저장되었습니다." });
            }
            catch (Exception ex)
            {
                return Results.InternalServerError(new { Error = ex.Message });
            }
        });

        // 인메모리 세션 스냅샷 파일 로드
        app.MapPost("/api/agents/load", (
            ConcurrentDictionary<string, AgentInstance> agents,
            IPersistenceService persistence) =>
        {
            try
            {
                var loadedAgents = persistence.LoadAllAgents();
                if (loadedAgents.Count > 0)
                {
                    agents.Clear();
                    foreach (var kv in loadedAgents)
                    {
                        agents[kv.Key] = kv.Value;
                    }
                    return Results.Ok(new { Message = "LiteDB로부터 최종 저장 상태를 성공적으로 로드했습니다." });
                }
                return Results.NotFound(new { Message = "LiteDB에 저장된 에이전트 데이터가 존재하지 않습니다." });
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
            var result = await scheduler.QueueDialogueTaskAsync(request.AgentIdA, request.AgentIdB, ct);
            
            if (!result.Success)
            {
                return Results.BadRequest(new { Error = result.ErrorMessage });
            }

            return Results.Ok(new
            {
                result.TaskId,
                Status = "Completed",
                result.Summary,
                result.StructuredLines,
                result.EmotionUpdates
            });
        });

        // 완료된 대화 결과 조회 (REST API 방식)
        app.MapGet("/api/interaction/result/{taskId}", (ulong taskId, InteractionScheduler scheduler) =>
        {
            if (scheduler.TryGetCompletedResult(taskId, out var result) && result != null)
            {
                return Results.Ok(new
                {
                    result.TaskId,
                    Status = result.Success ? "Completed" : "Failed",
                    result.Success,
                    result.ErrorMessage,
                    result.Summary,
                    result.StructuredLines
                });
            }

            return Results.Ok(new
            {
                TaskId = taskId,
                Status = "Processing",
                Message = "대화가 아직 진행 중이거나 큐에서 대기 중입니다."
            });
        });

        // 현재 대기 중이거나 진행 중인 작업 조회
        app.MapGet("/api/interaction/active", (InteractionScheduler scheduler) =>
        {
            return Results.Ok(scheduler.GetActiveAndPendingTasks());
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
            ConcurrentDictionary<string, AgentInstance> agents,
            IPersistenceService persistence) =>
        {
            if (!agents.TryGetValue(request.TargetAgentId, out var targetAgent))
            {
                return Results.NotFound(new { Error = $"대상 에이전트 '{request.TargetAgentId}'를 찾을 수 없습니다." });
            }

            var belief = new Belief
            {
                BeliefId = $"belief_{request.SubjectId}_{Guid.NewGuid().ToString().Substring(0, 5)}",
                SubjectId = request.SubjectId,
                Content = request.Content,
                Type = BeliefType.Heard,
                Confidence = 0.8,
                Salience = 1.0,
                EmotionalCharge = 0.5,
                SourceAgentId = "ExternalREST",
                AcquiredAt = DateTime.UtcNow
            };

            targetAgent.MemoryBox.AddOrUpdateBelief(belief);

            try
            {
                persistence.UpsertAgent(targetAgent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GossipInject Error] Failed to save agent state to LiteDB: {ex.Message}");
            }

            return Results.Ok(new { Message = $"에이전트 '{targetAgent.Persona.Name}'에게 소문 주입 완료" });
        });

        // 1. 루트 경로 진입 시 안내
        app.MapGet("/", () => Results.Ok("Project Mundus Vivens API Server is running. Dashboard is currently offline."));

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
    public record SendPlayerMessageApiRequest(ulong SessionId, string Message);
    public record EndPlayerDialogueApiRequest(ulong SessionId);


}
