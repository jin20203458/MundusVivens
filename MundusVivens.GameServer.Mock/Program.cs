using Grpc.Net.Client;
using MundusVivens.Prototype.Protos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.GameServer.Mock;

class Program
{
    private static readonly string[] Locations = { "성당 (Church)", "술집 (Tavern)", "광장 (Square)" };
    private static readonly Dictionary<string, string> NpcNames = new()
    {
        { "npc_kyle", "카일" },
        { "npc_eva", "에바" },
        { "npc_bart", "바르트" }
    };

    private static readonly Dictionary<string, string> CurrentLocations = new()
    {
        { "npc_kyle", "성당 (Church)" },
        { "npc_eva", "술집 (Tavern)" },
        { "npc_bart", "술집 (Tavern)" }
    };

    private static readonly Dictionary<string, string> CurrentActivities = new()
    {
        { "npc_kyle", "기도 중" },
        { "npc_eva", "맥주 컵 청소" },
        { "npc_bart", "술 마시기" }
    };



    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=======================================================");
        Console.WriteLine("🎮 Mundus Vivens — C++ Game Server Mock Client (C#)");
        Console.WriteLine("=======================================================\n");

        // 1. gRPC 채널 생성 (C# AI 서버 기본 gRPC 포트: 5001)
        const string grpcAddress = "http://localhost:5001";
        Console.WriteLine($"[Mock Server] {grpcAddress} 로 gRPC 채널을 생성하는 중...");
        
        using var channel = GrpcChannel.ForAddress(grpcAddress);
        var client = new MundusVivensGrpc.MundusVivensGrpcClient(channel);

        Console.WriteLine("[Mock Server] 통신 채널 준비 완료.");
        Console.WriteLine("[Mock Server] 5초마다 틱(Tick)을 생성하며 시뮬레이션을 시작합니다. (Ctrl+C로 종료)\n");

        int tick = 0;
        var random = new Random();

        while (true)
        {
            tick++;
            Console.WriteLine($"\n================== [ TICK {tick} ] ==================");

            // 1. 월드 틱 전송
            try
            {
                var tickRequest = new ProcessWorldTickRequest { TickNumber = tick };
                var tickResponse = await client.ProcessWorldTickAsync(tickRequest);
                Console.WriteLine($"⏱️ [Tick Sync] C# AI 서버에 틱 전달 완료: {tickResponse.Message}");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ [Error] C# AI 서버와 통신 실패 (서버가 켜져 있는지 확인하세요): {ex.Message}");
                Console.ResetColor();
                await Task.Delay(5000);
                continue;
            }

            // 2. NPC 이동 및 상태 동기화
            foreach (var npcId in NpcNames.Keys)
            {
                // 30% 확률로 다른 장소로 이동
                if (random.NextDouble() < 0.3)
                {
                    string oldLoc = CurrentLocations[npcId];
                    string newLoc = Locations[random.Next(Locations.Length)];
                    CurrentLocations[npcId] = newLoc;

                    // 이동에 따른 활동 변화 임의 지정
                    if (newLoc.Contains("Church"))
                        CurrentActivities[npcId] = "예배 참여 및 명상";
                    else if (newLoc.Contains("Tavern"))
                        CurrentActivities[npcId] = "사교 활동 및 휴식";
                    else
                        CurrentActivities[npcId] = "광장 산책 및 잡담";

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"🏃 [Movement] {NpcNames[npcId]}(이)가 [{oldLoc}] ➔ [{newLoc}] 이동 (활동: {CurrentActivities[npcId]})");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"🧍 [Status] {NpcNames[npcId]} -> {CurrentLocations[npcId]} 에 머무는 중 (활동: {CurrentActivities[npcId]})");
                }

                // gRPC를 통해 C# AI 서버로 상태 전송
                var statusRequest = new UpdateAgentStatusRequest
                {
                    AgentId = npcId,
                    Location = CurrentLocations[npcId],
                    Activity = CurrentActivities[npcId],
                    Emotion = "평온함" // 고정 또는 임의값
                };
                await client.UpdateAgentStatusAsync(statusRequest);
            }

            // 3. 인접 NPC 검출 및 대화 트리거
            var npcIds = NpcNames.Keys.ToList();
            for (int i = 0; i < npcIds.Count; i++)
            {
                for (int j = i + 1; j < npcIds.Count; j++)
                {
                    string npcA = npcIds[i];
                    string npcB = npcIds[j];

                    // 동일한 위치에 있는지 확인
                    if (CurrentLocations[npcA] == CurrentLocations[npcB])
                    {

                        // 50% 확률로 대화 발생
                        if (random.NextDouble() < 0.5)
                        {
                            // 대화 시작 전 상태 출력
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"\n💬 [Overlap Alert] {NpcNames[npcA]}와(과) {NpcNames[npcB]}(이)가 [{CurrentLocations[npcA]}]에서 마주쳤습니다!");
                            Console.WriteLine($"💬 대화를 트리거합니다. (C# AI 서버 gRPC 호출)...");
                            Console.ResetColor();

                            try
                            {
                                var dialogueRequest = new TriggerDialogueRequest
                                {
                                    AgentIdA = npcA,
                                    AgentIdB = npcB,
                                    WaitForCompletion = true
                                };

                                var response = await client.TriggerDialogueAsync(dialogueRequest);

                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"\n================== [ 대화 요약 ] ==================");
                                Console.WriteLine(response.DialogueSummary);
                                Console.WriteLine("==================================================");
                                Console.ResetColor();

                                Console.WriteLine("\n[대화 상세 로그]");
                                foreach (var line in response.DialogueLines)
                                {
                                    Console.WriteLine(line);
                                }
                                Console.WriteLine("==================================================\n");

                                // 대화 나눈 두 명은 현재 위치와 어울리는 행동 상태로 갱신
                                CurrentActivities[npcA] = "대화 마침";
                                CurrentActivities[npcB] = "대화 마침";


                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"❌ [Error] 대화 트리거 실패: {ex.Message}");
                                Console.ResetColor();
                            }
                        }
                    }
                }
            }

            // 5초 대기
            await Task.Delay(5000);
        }
    }


}
