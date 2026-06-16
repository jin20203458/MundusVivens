# 🌍 Project Mundus Vivens (살아 숨쉬는 세계)

NPC들이 스스로 기억하고, 판단하고, 소문을 퍼뜨리며 살아가는 자율 에이전트 사회 시뮬레이션 엔진입니다.

---

## 📂 프로젝트 구조

* **`MundusVivens.Prototype/`**: Phase 1 콘솔 기반 최소 프로토타입 프로젝트
  * **`Models/`**: 에이전트(`AgentInstance`), 성격(`Persona`), 상태(`AgentStatus`), 관계망(`Relationship`), 3단계 기억(`MemoryBox`), 소문(`GossipItem`) 등의 데이터 스키마 정의
  * **`Services/`**: Gemini API 연동(`GeminiApiService`), OAuth 인증(`GoogleAuthService`), 소문 매칭 및 변형(`GossipEngine`), 대화 오케스트레이션 및 사후 분석(`DialogueOrchestrator`) 서비스 구현
* **`MundusVivens.slnx`**: Visual Studio 2022용 솔루션 파일

---

## ⚡ 주요 구현 피처 (Phase 1)

1. **3단계 기억 시스템 (Memory System)**
   * **단기 기억**: 대화 진행 중 즉각 소멸하는 초경량 대사 핑퐁 버퍼
   * **중기 기억**: 최근 대화 및 사회적 만남을 1문장으로 요약한 에피소드 링 버퍼
   * **장기 기억**: 에이전트의 가치관과 정체성을 지배하는 극소수(최대 5개)의 핵심 기억
2. **호감도 및 신뢰도 관계망 (Relationship Graph)**
   * NPC 간 호감도(-100 ~ 100)와 신뢰도(0 ~ 100)를 관리하며, 대화 진행 결과에 따라 실시간 동적 갱신
3. **소문 전파 및 자연스러운 왜곡 (Gossip & Mutation)**
   * NPC의 외향성 및 친밀도를 바탕으로 적절한 소문을 발설
   * LLM 대화의 맥락에 따라 전파되는 과정에서 정보가 자연스럽게 왜곡되는 소문 변형(Mutation) 기능 탑재
4. **하이브리드 LLM을 통한 토큰 비용 제어**
   * 메인 대화에는 `gemini-3.5-flash`를, 무거운 요약/추출/JSON 파싱에는 `gemini-3.1-flash-lite-preview`를 사용하여 토큰당 발생 비용을 극단적으로 절감

---

## 🚀 실행 방법

Visual Studio 2022에서 `MundusVivens.slnx` 솔루션을 열어 실행하거나, 터미널에서 다음 명령어를 입력합니다.

```bash
cd MundusVivens.Prototype
dotnet run
```

> ⚠️ **실행 요구 사항**: 실행을 위해 `MundusVivens.Prototype/Config/google-credentials.json` 인증 파일과 `MundusVivens.Prototype/AppSettings.json` 파일에 올바른 API 설정이 존재해야 합니다. (로컬 환경 복사 완료)