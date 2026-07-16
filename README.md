# Project Mundus Vivens (AI Server)

Project Mundus Vivens는 NPC들이 스스로 기억을 관리하고 사회적 상호작용을 수행하는 자율 에이전트 시뮬레이션 엔진입니다. 이 저장소는 C# 기반의 AI 백엔드 서버를 포함하고 있습니다.

## 기술 문서 (Documentation)

프로젝트 전체 시스템 아키텍처, 에이전트 인지 모델 및 향후 개발 로드맵은 `Obsidian.Agent` 저장소에 중앙 집중화되어 관리됩니다. 아래 문서들은 개발자와 코딩 에이전트 모두가 참조하는 **단일 진실의 원천(Single Source of Truth)**입니다.

- [00_project_overview.md](../Obsidian.Agent/MundusVivens/docs/00_project_overview.md): 전체 시스템 아키텍처 및 통신망
- [01_game_server_architecture.md](../Obsidian.Agent/MundusVivens/docs/01_game_server_architecture.md): C++ 물리 엔진 아키텍처 (ECS, 스레드 모델)
- [02_agent_design.md](../Obsidian.Agent/MundusVivens/docs/02_agent_design.md): C# 인지 파이프라인 (LLM, 기억, 대화)
- [03_future_roadmap.md](../Obsidian.Agent/MundusVivens/docs/03_future_roadmap.md): 향후 구현 로드맵
- [04_cpp_server_profiling.md](../Obsidian.Agent/MundusVivens/docs/04_cpp_server_profiling.md): Tracy 프로파일러 연동 및 4대 벤치마크 검증 보고서
- [05_csharp_ai_profiling.md](../Obsidian.Agent/MundusVivens/docs/05_csharp_ai_profiling.md): C# AI 대뇌 서버 아키텍처 효율성 및 비용 정량 비교 보고서


> **참고**:
 전체 시스템 구성도 및 흐름도는 중복을 방지하기 위해 [00_project_overview.md](../Obsidian.Agent/MundusVivens/docs/00_project_overview.md)에서만 제공합니다.

## 주요 기능

- **다중 계층 기억 시스템**: 단기, 중기, 장기(Core) 믿음을 관리하며 시간에 따른 자연스러운 쇠퇴(Decay)와 망각 로직을 처리합니다.
- **동적 관계망**: NPC 간의 대화 결과에 따라 호감도와 신뢰도 수치를 실시간으로 갱신합니다.
- **소문 전파 및 변형**: 에이전트 간의 정보 전달과 그 과정에서 발생하는 자연스러운 정보의 왜곡을 시뮬레이션합니다.
- **LLM 오케스트레이션**: 언어 모델을 활용하여 실시간 대화를 생성하고 일일 스케줄링을 위한 성찰(Reflection)을 수행합니다.

## 빌드 및 실행

1. Visual Studio 2022에서 `MundusVivens.slnx` 솔루션 파일을 엽니다.
2. API 인증 방식을 선택하여 설정합니다 (아래 **API 설정 가이드** 참고).
3. IDE에서 실행하거나 터미널을 통해 아래 명령어로 구동할 수 있습니다.

   ```bash
   dotnet run --project MundusVivens.Prototype
   ```

---

## 퀵스타트 & 로컬 벤치마크 (Quickstart & Local Benchmarks)

**Gemini API Key나 복잡한 GCP 권한 설정 없이**, 아래 벤치마크 명령어를 통해 C# AI 서버의 성능 최적화 핵심 코드를 로컬에서 즉시 구동하고 실측 결과를 확인할 수 있습니다.

### 1. 인과 캐스케이드 (Causal Cascade) 감쇄 연산 테스트
수많은 신념들 사이에서 인과 관계를 따라 확신도가 도미노식으로 전파되는 최적화 알고리즘의 동작 성능을 측정합니다. (깊이 가드 작동 확인 가능)
```bash
# 156개 노드 트리 구조 상에서 깊이 Clamping 가드를 적용하여 연산 속도를 검증합니다.
dotnet run --project MundusVivens.Prototype -- --benchmark-mode cascade 3 5
```

### 2. 메모리 아키텍처 (Hot/Cold Memory Box) 테스트
대량의 기억 노드(최대 1,220만 개)가 생성될 때 Hot/Cold 캐시 레이어가 작동하여 메모리 OOM 크래시를 완벽히 방지하는 스케일링 성능을 실측합니다.
```bash
dotnet run --project MundusVivens.Prototype -- --benchmark-mode memory 10
```

### 3. 성찰(Reflection) 스케줄러 바인딩 테스트
에이전트가 하루 동안 겪은 기억들에 고유 식별자(ID)를 포함하여 LLM 성찰을 수행하는 로직을 테스트합니다. (※ 이 테스트는 Gemini API 키 설정이 필요합니다.)
```bash
dotnet run --project MundusVivens.Prototype -- --benchmark-mode reflection 1
```

### API 설정 가이드 (API Configuration)

본 서버는 두 가지 방식의 Gemini API 연동을 지원합니다. 환경에 맞는 방식을 `appsettings.json` 파일에 구성하세요.

#### 옵션 1: Google AI Studio API Key 방식 (외부 권장 💡)
구글 클라우드 계정이나 복잡한 설정 없이, 가장 간편하게 API 키만 발급받아 연동하는 방식입니다.
1. `MundusVivens.Prototype/appsettings.json` 파일을 엽니다.
2. 설정을 다음과 같이 변경하고 발급받은 API 키를 입력합니다:
   ```json
   "UseVertexAI": false,
   "ApiKey": "발급받은_Gemini_API_Key"
   ```

#### 옵션 2: Google Cloud Vertex AI 방식 (기본값 ☁️)
구글 클라우드 플랫폼(GCP) 인프라를 사용하는 엔터프라이즈급 연동 방식입니다.
1. `MundusVivens.Prototype/appsettings.json` 파일에서 설정을 확인합니다:
   ```json
   "UseVertexAI": true,
   "ProjectId": "구글_클라우드_프로젝트_ID"
   ```
2. 구글 클라우드 콘솔에서 서비스 계정(Service Account) 키를 JSON 파일로 다운로드합니다.
3. 해당 키 파일의 이름을 `google-credentials.json`으로 변경한 뒤, `MundusVivens.Prototype/Config/` 디렉토리 하위에 위치시킵니다. (보안을 위해 `.gitignore`에 등록되어 커밋되지 않습니다.)