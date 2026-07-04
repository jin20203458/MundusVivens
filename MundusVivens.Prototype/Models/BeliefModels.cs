using System;
using System.Collections.Generic;

namespace MundusVivens.Prototype.Models;

public enum BeliefType
{
    Core,        // 정체성/신념 (쇠퇴안함, Eviction 면역)
    Witnessed,   // 직접 목격 (느린 쇠퇴)
    Heard,       // 전해 들음 (보통 쇠퇴)
    Overheard    // 엿들음 (빠른 쇠퇴)
}

public class Belief
{
    public string BeliefId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;      // 정보의 주인공 (예: "npc_cedric")
    public string Content { get; set; } = string.Empty;        // 실제 정보 내용
    public BeliefType Type { get; set; }
    
    // === 중요 수치 파라미터 (0.0 ~ 1.0) ===
    public double Confidence { get; set; } = 0.5;              // 확신도
    public double Salience { get; set; } = 1.0;                // 현저성 (머릿속 활성도)
    public double EmotionalCharge { get; set; } = 0.0;         // 정서적 충격/무게

    // === 출처 및 전파 경로 ===
    public string SourceAgentId { get; set; } = string.Empty;   // 나에게 알려준 NPC ID (Heard/Overheard)
    public List<string> PropagationPath { get; set; } = new();   // 누적 전파 경로 (A -> B -> C)
    public int MutationCount { get; set; } = 0;                // 와전(변형) 횟수
    
    // === 발설 완료 대상 추적 (중복 발화 방지) ===
    public HashSet<string> SharedWith { get; set; } = new();

    // === 시간 태그 ===
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;

    // === 임베딩 벡터 캐시 ===
    public float[]? ContentEmbedding { get; set; }

    // === 계산식 중요도 (우선순위 및 삭제 판단 기준) ===
    public double Importance => (Confidence * 0.4) + (Salience * 0.35) + (EmotionalCharge * 0.25);
}
