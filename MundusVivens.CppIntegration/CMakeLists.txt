cmake_minimum_required(VERSION 3.15)
project(MundusVivensGameServer CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# -------------------------------------------------------------------------
# gRPC and Protobuf Dependency Setup
# -------------------------------------------------------------------------
# 이 프로젝트는 gRPC와 Protobuf가 시스템에 설치되어 있거나 vcpkg가 활성화되어 있다고 가정합니다.
# vcpkg와 연동하려면 빌드 시 아래 옵션을 추가해 주세요:
#   cmake -DCMAKE_TOOLCHAIN_FILE=[vcpkg_path]/scripts/buildsystems/vcpkg.cmake ..
# -------------------------------------------------------------------------

find_package(gRPC CONFIG REQUIRED)
find_package(Protobuf CONFIG REQUIRED)

# Protobuf 컴파일러 위치 찾기
set(PROTOC_PATH $<TARGET_FILE:protobuf::protoc>)
set(GRPC_CPP_PLUGIN_PATH $<TARGET_FILE:gRPC::grpc_cpp_plugin>)

# .proto 파일 위치 지정
set(PROTO_SRC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/Protos")
set(PROTO_FILE "${PROTO_SRC_DIR}/mundus_vivens.proto")

# 빌드 타겟 경로 정의
set(GEN_SRC_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY ${GEN_SRC_DIR})

# Generated Files
set(GEN_PROTO_SRC "${GEN_SRC_DIR}/mundus_vivens.pb.cc")
set(GEN_PROTO_HDR "${GEN_SRC_DIR}/mundus_vivens.pb.h")
set(GEN_GRPC_SRC "${GEN_SRC_DIR}/mundus_vivens.grpc.pb.cc")
set(GEN_GRPC_HDR "${GEN_SRC_DIR}/mundus_vivens.grpc.pb.h")

# Custom Command for Protobuf Compilation
add_custom_command(
    OUTPUT "${GEN_PROTO_SRC}" "${GEN_PROTO_HDR}" "${GEN_GRPC_SRC}" "${GEN_GRPC_HDR}"
    COMMAND protobuf::protoc
    ARGS --proto_path="${PROTO_SRC_DIR}"
         --cpp_out="${GEN_SRC_DIR}"
         --grpc_out="${GEN_SRC_DIR}"
         --plugin=protoc-gen-grpc="${GRPC_CPP_PLUGIN_PATH}"
         "${PROTO_FILE}"
    DEPENDS "${PROTO_FILE}"
    COMMENT "Generating gRPC and Protobuf C++ sources from ${PROTO_FILE}"
)

# -------------------------------------------------------------------------
# Target definition
# -------------------------------------------------------------------------

add_executable(MundusVivensGameServer
    main.cpp
    MundusVivensClient.cpp
    MundusVivensClient.h
    ${GEN_PROTO_SRC}
    ${GEN_PROTO_HDR}
    ${GEN_GRPC_SRC}
    ${GEN_GRPC_HDR}
)

target_include_directories(MundusVivensGameServer PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${GEN_SRC_DIR}
)

# Link gRPC and Protobuf libraries
target_link_libraries(MundusVivensGameServer PRIVATE
    gRPC::grpc++
    protobuf::libprotobuf
)
