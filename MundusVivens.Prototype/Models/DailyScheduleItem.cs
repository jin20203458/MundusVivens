namespace MundusVivens.Prototype.Models;

public class DailyScheduleItem
{
    public int StartHour { get; set; }
    public int EndHour { get; set; }
    public string TargetLocation { get; set; } = string.Empty;
    public string Activity { get; set; } = string.Empty;
}
