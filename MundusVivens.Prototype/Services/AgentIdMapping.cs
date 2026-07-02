using System;
using System.Collections.Generic;

namespace MundusVivens.Prototype.Services;

public static class AgentIdMapping
{
    private static readonly Dictionary<string, uint> StringToNumericMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "player", 1 },
        { "npc_eva", 2 },
        { "npc_kyle", 3 },
        { "npc_bart", 4 },
        { "npc_aileen", 5 },
        { "npc_cedric", 6 },
        { "npc_hugo", 7 },
        { "npc_lucas", 8 },
        { "npc_lyra", 9 },
        { "npc_maya", 10 },
        { "npc_valac", 11 }
    };

    private static readonly Dictionary<uint, string> NumericToStringMap = new();

    static AgentIdMapping()
    {
        foreach (var kvp in StringToNumericMap)
        {
            NumericToStringMap[kvp.Value] = kvp.Key;
        }
    }

    public static uint GetNumericId(string stringId)
    {
        if (string.IsNullOrWhiteSpace(stringId)) return 0;
        if (StringToNumericMap.TryGetValue(stringId, out var id))
        {
            return id;
        }
        return 0;
    }

    public static string GetStringId(uint numericId)
    {
        if (NumericToStringMap.TryGetValue(numericId, out var stringId))
        {
            return stringId;
        }
        return string.Empty;
    }
}
