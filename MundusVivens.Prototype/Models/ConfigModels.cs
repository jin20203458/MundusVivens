namespace MundusVivens.Prototype.Models;

public enum ModelTier
{
    Pro,
    Flash35,
    FlashLite,
    Flash3
}

public class AppSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Location { get; set; } = "asia-northeast3";
    public bool UseVertexAI { get; set; } = true;
    public string SelectedModel { get; set; } = "Flash35"; // "Flash35", "Pro", etc.
    public string SafetyThreshold { get; set; } = "BLOCK_NONE";
}
