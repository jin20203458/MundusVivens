using System.Collections.Generic;

namespace MundusVivens.Prototype.Models;

public class Relationship
{
    public string TargetAgentId { get; set; } = string.Empty;
    public int Liking { get; set; } = 0;   // -100 ~ 100
    public int Trust { get; set; } = 50;   // 0 ~ 100
    public List<string> Tags { get; set; } = new();
}
