# 🚀 Mundus Vivens: Phase 7 Realism Expansion Roadmap

이 문서는 AI 에이전트 아키텍처 연구를 바탕으로 **1인 개발 환경에 맞게 현실화한 Phase 7 개발 과제 목록(Living Roadmap)**입니다. 
오버엔지니어링(SQLite/FAISS/24시간 cross-server 시뮬레이션 등)을 배제하고, 실제 유저 체감 퀄리티(UX) 및 인지 정합성이 가장 높은 과제들을 3단계로 나누어 순차적으로 구현합니다.

---

## 🎯 Implementation Roadmap Overview

- **Phase 7.1 (몰입감 및 반응성 극대화)**: C++ 지연 마스킹 & 긴급 인터럽트 시스템
- **Phase 7.2 (인지 정합성 고도화)**: C# DAG 기반 믿음 연쇄 파기 & 소문 격리 버퍼
- **Phase 7.3 (월드 스케줄링 최적화)**: 정적 장소 인원 캡 검사 & 경량 공간 필터링

---

## <phase_7_1_latency_masking>
### Phase 7.1: LLM 지연 마스킹 및 로컬 긴급 인터럽트 (C++ Game Server Focus)

LLM 응답 대기 시간(2~4초) 동안 에이전트가 돌부처처럼 굳는 현상을 방지하고, 위급 상황 발생 시 50ms 이내로 즉시 반응하게 합니다.

*   `[PLANNED]` **C++ EnTT 로컬 대기 행동(Idle Fidgeting) 연동**: C++ 서버의 `BusyTag` 활성화 중, 무시하는 대신 주변 둘러보기, 가벼운 제스처, 서성임 등 로컬 대기 연출을 20Hz 루프에서 실시간 렌더링.
*   `[PLANNED]` **gRPC Emergency Interrupt (`grpc::ClientContext::TryCancel`)**: 대화/일정 추론 대기 중 플레이어의 물리적 타격이나 화재 등 위협 발생 시, 즉시 비동기 gRPC 요청을 취소하고 로컬 전투/도망 행동 상태 머신으로 제어권을 강제 가로채기.
</phase_7_1_latency_masking>

---

## <phase_7_2_cognitive_realism>
### Phase 7.2: DAG 기반 믿음 연쇄 파기 및 소문 격리 버퍼 (C# AI Engine Focus)

NPC가 낡고 모순된 과거 정보를 계속 보유하는 결함을 수정하고, 허무맹랑한 소문을 듣자마자 맹목적으로 믿는 현상을 방지합니다.

*   `[PLANNED]` **DAG 기반 믿음 연쇄 파기 (`MemoryBox.EvictCascade`)**: `Belief` 모델에 `DependsOn`, `Supersedes` 메타데이터를 추가. 상위 핵심 사실(Core Belief)이 변경되면 이와 모순되거나 연관된 하위 기억들을 연쇄적으로 일괄 파기/강등.
*   `[PLANNED]` **소문 격리 버퍼 (`RumorSandbox`)**: 타인에게 들은 소문(`Heard`, `Overheard`)을 즉시 핵심 지식으로 인정하지 않고 완충 버퍼에 보관. 타인의 교차 증언이나 일일 성찰(Reflection)을 거쳐 검증된 소문만 `Core Belief`로 승격.
</phase_7_2_cognitive_realism>

---

## <phase_7_3_schedule_optimization>
### Phase 7.3: 월드 스케줄링 최적화 및 경량 공간 필터링 (C# & C++ Shared)

과도한 백그라운드 시뮬레이션 연산 대신, 실용적인 룰 기반 캡(Capacity)과 필터링으로 스케줄 엉킴 및 과부하를 예방합니다.

*   `[PLANNED]` **장소별 정적 인원 캡 검사 (`DailyPlanService`)**: LLM이 스케줄을 생성할 때 특정 시간에 동일 장소(예: 광장, 술집)로 과도한 인원이 몰리지 않도록 정적 인원 수 캡을 검사하고 자동 조율.
*   `[PLANNED]` **기억 모델 내 경량 공간 태깅**: Complex DB(SQLite/FAISS) 없이, `Belief` 모델에 장소 태그/좌표 정보를 가볍게 부착하여 LINQ 수준에서 고성능 공간 필터링 지원.
</phase_7_3_schedule_optimization>
