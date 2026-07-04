# 🚀 Mundus Vivens: Phase 7 Realism Expansion Roadmap

이 문서는 2025/2026년 최신 AI 에이전트 아키텍처 연구(eMEM, MAGMA, ARTEM 등)를 기반으로 작성된 **차기 개발 과제 목록(Living Roadmap)**입니다. 코딩 에이전트는 향후 기능 추가 시 이 문서의 `[PLANNED]` 항목들을 실제 `[IMPLEMENTED]`로 전환하는 작업을 수행합니다.

---

## <memory_engine_upgrade>
### 1. 다중 인덱스 기억 저장소 (eMEM Architecture)

현재 평면적인 딕셔너리(`ConcurrentDictionary`) 형태인 기억 저장소를 입체적으로 확장하여, 시공간 쿼리가 즉각적으로 가능하게 만듭니다.

*   `[PLANNED]` **SQLite 관계형 인덱스 도입**: 인물, 객체, 행동(Who, What, Action)을 릴레이셔널 테이블로 매핑하여 명시적 질의 최적화.
*   `[PLANNED]` **HNSW 벡터 인덱스 연동**: 단순 코사인 유사도를 넘어, FAISS나 HNSW를 도입하여 대규모 기억 중 가장 맥락이 유사한 과거 에피소드를 $O(\log N)$에 검색.
*   `[PLANNED]` **R-Tree 공간 인덱스**: 에이전트가 "이 공간 근처에서 일어난 일"을 3D Bounding Box 반경(예: 20m 이내)으로 즉시 필터링할 수 있도록 R-Tree 캐싱 도입.
</memory_engine_upgrade>

---

## <belief_revision_graph>
### 2. 믿음 의존성 그래프 구축 (MAGMA & AGM Theory)

현재 단순 `Confidence` 수치 연산에 의존하는 믿음 병합 로직을, 상호 의존성을 추적하는 **인지 그래프(Belief Dependency Graph)** 구조로 고도화합니다.

*   `[PLANNED]` **메타데이터 의존성 엣지 추가**: `Belief` 클래스 내부에 `List<string> DependsOn`, `List<string> Supersedes` 속성을 추가.
*   `[PLANNED]` **연쇄 수정 및 소멸(Cascade Eviction)**: 새로운 Core 사실(예: "왕은 죽었다")이 기존의 다른 사실("왕은 매일 산책한다")과 `Contradicts` 엣지로 충돌할 때, 하위 믿음들을 AGM 이론에 따라 일괄적으로 자동 강등(Demote)하거나 삭제.
</belief_revision_graph>

---

## <spatio_temporal_grounding>
### 3. 시공간 정합성 및 이벤트 쿼리 (ARTEM)

에이전트가 단순히 좌표로 이동하는 것을 넘어, **시간과 공간이 결합된 연속적 사건(STEM)**을 기억하고 인출하게 만듭니다.

*   `[PLANNED]` **Temporal Window 기능**: `AcquiredAt` 타임스탬프를 단순 저장용이 아니라 쿼리용(`Between 14:00 and 16:00`)으로 검색 인덱스화.
*   `[PLANNED]` **Deliberation Sandbox (심의 구역)**: 타인에게서 전해 들은 소문(`Heard`, `Overheard`)이 장기 기억(Core)으로 승격되기 전, 신뢰도 검증을 거치는 샌드박스 버퍼 시스템 도입.
</spatio_temporal_grounding>

---

## <simulation_sandbox>
### 4. 일일 스케줄 프리롤아웃 (L2 Simulator)

LLM이 24시간 스케줄을 텍스트로 찍어내는 단계를 넘어, 정신적인 샌드박스 환경에서 갈등을 예측하는 시뮬레이터 기능을 도입합니다.

*   `[PLANNED]` **L2 Simulator 도입**: `DailyPlanService`가 스케줄을 확정하기 전, 서버 내부의 별도 백그라운드 샌드박스에서 이동 동선과 인구 밀집도를 미리 시뮬레이션(Pre-Rollout). 병목이나 대인 관계 충돌이 예상되면 스케줄을 자체 폐기하고 2차 재생성(L3 Evolver 루프).
*   `[PLANNED]` **행동 거부(Veto) 로직 확장**: 현재 `RollMeditate(80% 거부)`와 같은 룰 기반 행동 캔슬을 넘어, 시뮬레이션 결과에 기반한 자율적 Veto 시스템 구축.
</simulation_sandbox>
