using System.Collections.Generic;

namespace MundusVivens.Prototype.Models;

public class AgentInstance
{
    public string AgentId { get; set; } = string.Empty;
    public Persona Persona { get; set; } = new();
    public AgentStatus Status { get; set; } = new();
    public MemoryBox MemoryBox { get; set; } = new();
    
    // 이 NPC가 알고 있는 소문 목록 (GossipId -> KnownGossip)
    public Dictionary<string, KnownGossip> KnownGossips { get; set; } = new();
    
    // 타인에 대한 관계 그래프 (TargetAgentId -> Relationship)
    public Dictionary<string, Relationship> RelationshipMap { get; set; } = new();
}
