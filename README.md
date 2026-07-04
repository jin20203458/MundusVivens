# Project Mundus Vivens (AI Server)

Project Mundus Vivens는 NPC들이 스스로 기억을 관리하고 사회적 상호작용을 수행하는 자율 에이전트 시뮬레이션 엔진입니다. 이 저장소는 C# 기반의 AI 백엔드 서버를 포함하고 있습니다.

## 기술 문서 (Documentation)

시스템 아키텍처, 에이전트 인지 모델 및 향후 개발 로드맵은 `docs/` 디렉토리에 정의되어 있습니다. 이 문서들은 개발자와 코딩 에이전트 모두가 참조하는 단일 진실의 원천(Single Source of Truth)입니다.

- [01_architecture.md](docs/01_architecture.md)
- [02_agent_design.md](docs/02_agent_design.md)
- [03_phase7_roadmap.md](docs/03_phase7_roadmap.md)

## 주요 기능

- **다중 계층 기억 시스템**: 단기, 중기, 장기(Core) 믿음을 관리하며 시간에 따른 자연스러운 쇠퇴(Decay)와 망각 로직을 처리합니다.
- **동적 관계망**: NPC 간의 대화 결과에 따라 호감도와 신뢰도 수치를 실시간으로 갱신합니다.
- **소문 전파 및 변형**: 에이전트 간의 정보 전달과 그 과정에서 발생하는 자연스러운 정보의 왜곡을 시뮬레이션합니다.
- **LLM 오케스트레이션**: 언어 모델을 활용하여 실시간 대화를 생성하고 일일 스케줄링을 위한 성찰(Reflection)을 수행합니다.

## 빌드 및 실행

1. Visual Studio 2022에서 `MundusVivens.slnx` 솔루션 파일을 엽니다.
2. 실행을 위해 `MundusVivens.Prototype/Config/google-credentials.json` 및 `AppSettings.json` 파일에 올바른 API 설정이 존재하는지 확인합니다.
3. IDE에서 실행하거나 터미널을 통해 아래 명령어로 구동할 수 있습니다.

   ```bash
   cd MundusVivens.Prototype
   dotnet run
   ```