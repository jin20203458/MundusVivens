#include <iostream>
#include <thread>
#include <chrono>
#include "MundusVivensClient.h"

int main() {
    std::cout << "Mundus Vivens C++ Client Reference Starter" << std::endl;
    std::cout << "connecting to localhost:5001..." << std::endl;

    MundusVivens::MundusVivensClient client("localhost:5001");

    // 1. 에이전트 상태 조회 예제
    std::cout << "\n--- 1. GetAgentStatus Test ---" << std::endl;
    auto status = client.GetAgentStatus("npc_eva");
    std::cout << "Name: " << status.name << std::endl;
    std::cout << "Location: " << status.location << std::endl;
    std::cout << "Emotion: " << status.emotion << std::endl;
    std::cout << "Activity: " << status.activity << std::endl;

    // 2. 월드 틱 전송 및 위치 업데이트 예제
    std::cout << "\n--- 2. Update status and tick Test ---" << std::endl;
    std::string out_msg;
    bool success = client.UpdateAgentStatus("npc_kyle", "술집 (Tavern)", "피곤함", "바르트의 말 경청", out_msg);
    std::cout << "UpdateStatus: " << (success ? "Success" : "Failed") << " - Message: " << out_msg << std::endl;

    success = client.ProcessWorldTick(42, out_msg);
    std::cout << "ProcessTick: " << (success ? "Success" : "Failed") << " - Message: " << out_msg << std::endl;

    // 3. 대화 트리거 예제
    std::cout << "\n--- 3. Trigger Dialogue Test ---" << std::endl;
    std::cout << "Triggering dialogue between kyle and eva..." << std::endl;
    auto dialogue = client.TriggerDialogue("npc_kyle", "npc_eva", true);
    
    std::cout << "Dialogue Completed. TaskId: " << dialogue.task_id << std::endl;
    std::cout << "Summary: " << dialogue.dialogue_summary << std::endl;
    std::cout << "Dialogue Lines:" << std::endl;
    for (const auto& line : dialogue.dialogue_lines) {
        std::cout << "  " << line << std::endl;
    }

    return 0;
}
