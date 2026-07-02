namespace MundusVivens.Prototype.Models;

public class GossipItem
{
    public string GossipId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;      // 소문의 주인공 (예: "카일")
    public string Content { get; set; } = string.Empty;      // 소물 내용
    public string SourceAgentId { get; set; } = string.Empty;// 최초 유포자
    public int BaseCredibility { get; set; } = 50;           // 최초 정보 신뢰도 (0 ~ 100)
    public int MutationCount { get; set; } = 0;              // 변형 횟수
    public float[]? ContentEmbedding { get; set; }           // 🆕 임베딩 벡터 캐시
}

public class KnownGossip
{
    public GossipItem Gossip { get; set; } = new();
    public double SubjectiveBelief { get; set; } = 0.5;      // 주관적 확신도 (0.0 ~ 1.0)
    public bool HasSharedWithOthers { get; set; } = false;   // 다른이에게 발설 여부

    // 🆕 소문 전파 경로 추적 필드 (4-B-6)
    public string DirectInformantAgentId { get; set; } = string.Empty; // 직전 유포자 (누가 말해주었는가)
    public List<string> PropagationPath { get; set; } = new();         // 누적 전파 경로 (A ➔ B ➔ C)

    // 🆕 소문 쇠퇴 추적용 타임스탬프 및 틱 번호
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;        // 최초 획득 시각
    public DateTime LastReinforcedAt { get; set; } = DateTime.UtcNow;   // 마지막 재확인 시각
    public int LastDecayedAtTick { get; set; } = 0;                     // 마지막 쇠퇴 계산이 수행된 틱 번호
}
