namespace MundusVivens.Prototype.Models;

public class AgentStatus
{
    public string CurrentLocation { get; set; } = "Unknown";
    public string Emotion { get; set; } = "평온함";
    public string Activity { get; set; } = "대기 중";
    public bool IsInConversation { get; set; } = false;

    // 🚀 Axis 2: Job 관리용 정보
    public ulong ActiveJobId { get; set; } = 0;
    public string ActiveJobLocation { get; set; } = string.Empty;
    public string ActiveJobIntent { get; set; } = string.Empty;
    public bool HasActiveJob => ActiveJobId > 0;
    public int LastCompletedHour { get; set; } = -1;
}
