namespace MundusVivens.Prototype.Models;

public class Persona
{
    public string Name { get; set; } = string.Empty;
    public string Job { get; set; } = string.Empty;
    public string ToneStyle { get; set; } = string.Empty;
    public string Backstory { get; set; } = string.Empty;
    public string CoreValues { get; set; } = string.Empty;
    public double Extroversion { get; set; } = 0.5; // 0.0 ~ 1.0 (외향성 지수)
    public string Faction { get; set; } = string.Empty; // 소속 진영/파벌 (예: "성당 세력", "경비대")
}
