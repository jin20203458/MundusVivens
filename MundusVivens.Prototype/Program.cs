using Microsoft.Extensions.DependencyInjection;
using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace MundusVivens.Prototype;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=======================================================");
        Console.WriteLine("🌍 Project Mundus Vivens — Phase 1 콘솔 시뮬레이터");
        Console.WriteLine("=======================================================\n");

        // 1. DI 컨테이너 설정
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<IGoogleAuthService, GoogleAuthService>();
        services.AddSingleton<IGeminiApiService, GeminiApiService>();
        services.AddSingleton<IGossipEngine, GossipEngine>();
        services.AddSingleton<IDialogueOrchestrator, DialogueOrchestrator>();
        
        var serviceProvider = services.BuildServiceProvider();
        var orchestrator = serviceProvider.GetRequiredService<IDialogueOrchestrator>();
        var apiService = serviceProvider.GetRequiredService<IGeminiApiService>();

        // 2. 에이전트 초기화
        var agents = InitializeAgents();

        // 3. 소문 시딩 (에바가 카일에 대한 소문을 이미 알고 있음)
        SeedGossip(agents);

        // 4. 시나리오 실행
        try
        {
            // 1단계: 에바와 바르트가 술집에서 만남
            Console.WriteLine("▶ [시나리오 1단계] 술집에서 에바와 바르트의 조우 및 대화");
            agents["npc_eva"].Status.CurrentLocation = "술집 (Tavern)";
            agents["npc_bart"].Status.CurrentLocation = "술집 (Tavern)";
            await orchestrator.RunConversationAsync(agents["npc_eva"], agents["npc_bart"]);

            // 2단계: 바르트가 성당에 가서 카일을 만남
            Console.WriteLine("▶ [시나리오 2단계] 성당에서 바르트와 카일의 조우 및 대화");
            agents["npc_bart"].Status.CurrentLocation = "성당 (Church)";
            agents["npc_kyle"].Status.CurrentLocation = "성당 (Church)";
            await orchestrator.RunConversationAsync(agents["npc_bart"], agents["npc_kyle"]);

            // 5. 최종 데이터 분석 출력
            PrintFinalStatusReport(agents, apiService);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[Critical Error] 시뮬레이션 중 치명적 오류 발생: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static Dictionary<string, AgentInstance> InitializeAgents()
    {
        var dict = new Dictionary<string, AgentInstance>();

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

    private static void SeedGossip(Dictionary<string, AgentInstance> agents)
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

    private static void PrintFinalStatusReport(Dictionary<string, AgentInstance> agents, IGeminiApiService apiService)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("📊 시뮬레이션 종료: 전체 에이전트 상태 최종 보고서");
        Console.WriteLine("=======================================================");

        foreach (var kvp in agents)
        {
            var agent = kvp.Value;
            Console.WriteLine($"\n👤 [{agent.Persona.Name}] ({agent.Persona.Job})");
            Console.WriteLine($"   * 감정 상태: {agent.Status.Emotion}");
            Console.WriteLine($"   * 최근 에피소드 기억:");
            if (agent.MemoryBox.EpisodicMemories.Any())
            {
                foreach (var ep in agent.MemoryBox.EpisodicMemories)
                {
                    Console.WriteLine($"     - [{ep.Timestamp:HH:mm}] 대상: {ep.TargetName} / 내용: {ep.Summary}");
                }
            }
            else
            {
                Console.WriteLine("     - 없음");
            }

            Console.WriteLine($"   * 관계 그래프:");
            foreach (var relKvp in agent.RelationshipMap)
            {
                var targetName = agents[relKvp.Key].Persona.Name;
                Console.WriteLine($"     - ➔ {targetName}: 호감도 {relKvp.Value.Liking}, 신뢰도 {relKvp.Value.Trust}");
            }

            Console.WriteLine($"   * 알고 있는 소문 목록:");
            if (agent.KnownGossips.Any())
            {
                foreach (var gossipKvp in agent.KnownGossips)
                {
                    var kg = gossipKvp.Value;
                    var subjectName = agents.ContainsKey(kg.Gossip.Subject) ? agents[kg.Gossip.Subject].Persona.Name : kg.Gossip.Subject;
                    Console.WriteLine($"     - 소문 대상: {subjectName} / 확신도: {kg.SubjectiveBelief:P0}");
                    Console.WriteLine($"       내용: \"{kg.Gossip.Content}\" (변형 횟수: {kg.Gossip.MutationCount})");
                }
            }
            else
            {
                Console.WriteLine("     - 없음");
            }
        }

        Console.WriteLine("\n=======================================================");
        Console.WriteLine("💸 API 사용량 및 비용 분석");
        Console.WriteLine("=======================================================");
        Console.WriteLine($"   * 총 Prompt 토큰 수: {apiService.TotalPromptTokens:N0} tokens");
        Console.WriteLine($"   * 총 Completion 토큰 수: {apiService.TotalCompletionTokens:N0} tokens");
        Console.WriteLine($"   * 총 사용 토큰 수: {apiService.TotalTokens:N0} tokens");
        Console.WriteLine($"   * 예상 발생 비용: ${apiService.ApproximateCostUsd:F6} (약 {apiService.ApproximateCostUsd * 1350:F2}원)");
        Console.WriteLine("=======================================================\n");
    }
}
