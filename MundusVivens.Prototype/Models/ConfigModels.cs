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

public class LocationConfig
{
    public string SemanticName { get; set; } = string.Empty;
    public System.Collections.Generic.List<string> Aliases { get; set; } = new();
    public CoordinatesConfig Coordinates { get; set; } = new();
}

public class CoordinatesConfig
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
