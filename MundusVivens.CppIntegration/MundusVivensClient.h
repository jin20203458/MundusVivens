#pragma once

#include <string>
#include <vector>
#include <memory>
#include <grpcpp/grpcpp.class.h>
#include "mundus_vivens.grpc.pb.h"

namespace MundusVivens {

struct DialogueResult {
    std::string task_id;
    bool is_queued = false;
    bool completed_immediately = false;
    std::string dialogue_summary;
    std::vector<std::string> dialogue_lines;
};

struct AgentStatus {
    std::string name;
    std::string location;
    std::string emotion;
    std::string activity;
    std::vector<std::string> memories;
};

class MundusVivensClient {
public:
    // C# AI 서버의 gRPC 엔드포인트(예: "localhost:5001")를 전달받아 클라이언트를 생성합니다.
    MundusVivensClient(const std::string& server_address);

    // NPC 간 대화를 트리거합니다.
    // wait_for_completion이 true이면 대화가 끝날 때까지 대기(동기적)하며 결과를 반환합니다.
    DialogueResult TriggerDialogue(const std::string& agent_id_a, const std::string& agent_id_b, bool wait_for_completion = true);

    // 특정 에이전트의 실시간 상태(위치, 감정, 에피소드 기억 요약 등)를 조회합니다.
    AgentStatus GetAgentStatus(const std::string& agent_id);

    // 에바와 같은 특정 에이전트에게 소문을 강제로 주입합니다.
    bool InjectGossip(const std::string& target_agent_id, const std::string& subject_id, const std::string& content, std::string& out_message);

    // C++ 게임 서버가 결정한 에이전트의 최신 상태(위치, 감정, 현재 상태)를 C# 서버로 동기화합니다.
    bool UpdateAgentStatus(const std::string& agent_id, const std::string& location, const std::string& emotion, const std::string& activity, std::string& out_message);

    // C++ 게임 서버의 틱(시간 흐름) 진행을 C# AI 서버에 알립니다.
    bool ProcessWorldTick(int32_t tick_number, std::string& out_message);

private:
    std::unique_ptr<mundusvivens::MundusVivensGrpc::Stub> stub_;
};

} // namespace MundusVivens
