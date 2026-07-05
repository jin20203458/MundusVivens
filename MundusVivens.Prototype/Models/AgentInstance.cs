using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MundusVivens.Prototype.Models;

public class AgentInstance
{
    public string AgentId { get; set; } = string.Empty;
    public uint NumericId => MundusVivens.Prototype.Services.AgentIdMapping.GetNumericId(AgentId);
    public Persona Persona { get; set; } = new();
    public AgentStatus Status { get; set; } = new();
    public MemoryBox MemoryBox { get; set; } = new();
    
    // 타인에 대한 관계 그래프 (TargetAgentId -> Relationship)
    public ConcurrentDictionary<string, Relationship> RelationshipMap { get; set; } = new();

    // 🆕 활성 일과 계획 및 대기 버퍼, 만료 틱 정보 (LiteDB에 자동 영구 저장)
    public List<DailyScheduleItem> CurrentSchedule { get; set; } = new();
    public List<DailyScheduleItem>? NextSchedule { get; set; }
    public int PlanExpirationTick { get; set; } = 0;

    // 🆕 최초 구동일(Day 1)에 사용할 사전 저작된 시차 분산형 스케줄
    public List<DailyScheduleItem> InitialSchedule { get; set; } = new();
}
