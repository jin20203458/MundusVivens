#include "MundusVivensClient.h"
#include <grpcpp/create_channel.h>
#include <iostream>

namespace MundusVivens {

MundusVivensClient::MundusVivensClient(const std::string& server_address) {
    // Kestrel gRPC 서버의 로컬 통신을 위해 보안 토큰이 없는 비보안 채널을 엽니다.
    auto channel = grpc::CreateChannel(server_address, grpc::InsecureChannelCredentials());
    stub_ = mundusvivens::MundusVivensGrpc::NewStub(channel);
}

DialogueResult MundusVivensClient::TriggerDialogue(const std::string& agent_id_a, const std::string& agent_id_b, bool wait_for_completion) {
    mundusvivens::TriggerDialogueRequest request;
    request.set_agent_id_a(agent_id_a);
    request.set_agent_id_b(agent_id_b);
    request.set_wait_for_completion(wait_for_completion);

    mundusvivens::TriggerDialogueResponse response;
    grpc::ClientContext context;

    grpc::Status status = stub_->TriggerDialogue(&context, request, &response);

    DialogueResult result;
    if (status.ok()) {
        result.task_id = response.task_id();
        result.is_queued = response.is_queued();
        result.completed_immediately = response.completed_immediately();
        result.dialogue_summary = response.dialogue_summary();
        
        for (int i = 0; i < response.dialogue_lines_size(); ++i) {
            result.dialogue_lines.push_back(response.dialogue_lines(i));
        }
    } else {
        std::cerr << "TriggerDialogue gRPC Error: " << status.error_message() << std::endl;
        result.dialogue_summary = "gRPC 에러 발생: " + status.error_message();
    }

    return result;
}

AgentStatus MundusVivensClient::GetAgentStatus(const std::string& agent_id) {
    mundusvivens::GetAgentStatusRequest request;
    request.set_agent_id(agent_id);

    mundusvivens::GetAgentStatusResponse response;
    grpc::ClientContext context;

    grpc::Status status = stub_->GetAgentStatus(&context, request, &response);

    AgentStatus result;
    if (status.ok()) {
        result.name = response.name();
        result.location = response.location();
        result.emotion = response.emotion();
        result.activity = response.activity();
        
        for (int i = 0; i < response.memories_size(); ++i) {
            result.memories.push_back(response.memories(i));
        }
    } else {
        std::cerr << "GetAgentStatus gRPC Error: " << status.error_message() << std::endl;
    }

    return result;
}

bool MundusVivensClient::InjectGossip(const std::string& target_agent_id, const std::string& subject_id, const std::string& content, std::string& out_message) {
    mundusvivens::InjectGossipRequest request;
    request.set_target_agent_id(target_agent_id);
    request.set_subject_id(subject_id);
    request.set_content(content);

    mundusvivens::InjectGossipResponse response;
    grpc::ClientContext context;

    grpc::Status status = stub_->InjectGossip(&context, request, &response);

    if (status.ok()) {
        out_message = response.message();
        return response.success();
    } else {
        out_message = "gRPC Error: " + status.error_message();
        return false;
    }
}

bool MundusVivensClient::UpdateAgentStatus(const std::string& agent_id, const std::string& location, const std::string& emotion, const std::string& activity, std::string& out_message) {
    mundusvivens::UpdateAgentStatusRequest request;
    request.set_agent_id(agent_id);
    request.set_location(location);
    request.set_emotion(emotion);
    request.set_activity(activity);

    mundusvivens::UpdateAgentStatusResponse response;
    grpc::ClientContext context;

    grpc::Status status = stub_->UpdateAgentStatus(&context, request, &response);

    if (status.ok()) {
        out_message = response.message();
        return response.success();
    } else {
        out_message = "gRPC Error: " + status.error_message();
        return false;
    }
}

bool MundusVivensClient::ProcessWorldTick(int32_t tick_number, std::string& out_message) {
    mundusvivens::ProcessWorldTickRequest request;
    request.set_tick_number(tick_number);

    mundusvivens::ProcessWorldTickResponse response;
    grpc::ClientContext context;

    grpc::Status status = stub_->ProcessWorldTick(&context, request, &response);

    if (status.ok()) {
        out_message = response.message();
        return response.success();
    } else {
        out_message = "gRPC Error: " + status.error_message();
        return false;
    }
}

} // namespace MundusVivens
