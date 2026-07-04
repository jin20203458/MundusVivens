using System;
using System.Collections.Concurrent;

namespace MundusVivens.Prototype.Services;

public static class AgentIdMapping
{
    private static readonly ConcurrentDictionary<string, uint> StringToNumericMap = new(new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
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
    }, StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<uint, string> NumericToStringMap = new();
    private static uint _nextId = 100;
    private static readonly object _lock = new();

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

        lock (_lock)
        {
            if (StringToNumericMap.TryGetValue(stringId, out id))
            {
                return id;
            }

            while (NumericToStringMap.ContainsKey(_nextId))
            {
                _nextId++;
            }

            id = _nextId++;
            StringToNumericMap[stringId] = id;
            NumericToStringMap[id] = stringId;
            Console.WriteLine($"[AgentIdMapping] Auto-registered dynamic NPC: '{stringId}' -> {id}");
            return id;
        }
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
