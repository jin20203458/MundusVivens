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
}
