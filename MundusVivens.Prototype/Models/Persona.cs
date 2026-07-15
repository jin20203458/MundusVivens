namespace MundusVivens.Prototype.Models;

public class Persona
{
    public string Name { get; set; } = string.Empty;
    public string Job { get; set; } = string.Empty;
    public string ToneStyle { get; set; } = string.Empty;
    public string Backstory { get; set; } = string.Empty;
    public string CoreValues { get; set; } = string.Empty;
    public double Extroversion { get; set; } = 0.5; // 0.0 ~ 1.0 (외향성 지수)
    public string LongTermGoal { get; set; } = string.Empty;
    public string CurrentDrive { get; set; } = string.Empty;
    public bool IsSentient { get; set; } = true; // 🆕 지성체 여부 (기본값: true, 몬스터/동물은 false)
}

