using System;
using System.Collections.Generic;
using MundusVivens.Prototype.Protos;

namespace MundusVivens.Prototype.Models;

public static class LocationCoordinateRegistry
{
    private static readonly Dictionary<string, (float X, float Y, float Z)> SemanticToCoords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "성당 (Church)", (10.0f, 0.0f, 50.0f) },
        { "술집 (Tavern)", (-15.0f, 0.0f, -5.0f) },
        { "광장 (Square)", (0.0f, 0.0f, 0.0f) },
        { "마을 광장 (Square)", (0.0f, 0.0f, 0.0f) },
        { "Unknown", (0.0f, 0.0f, 0.0f) }
    };

    public static (float X, float Y, float Z) GetCoordinates(string semanticName)
    {
        if (string.IsNullOrEmpty(semanticName)) return (0f, 0f, 0f);

        foreach (var kv in SemanticToCoords)
        {
            if (semanticName.Contains(kv.Key) || kv.Key.Contains(semanticName))
            {
                return kv.Value;
            }
        }
        return (0f, 0f, 0f);
    }

    public static LocationInfo CreateLocationInfo(string name)
    {
        var (x, y, z) = GetCoordinates(name);
        return new LocationInfo
        {
            Name = name ?? "Unknown",
            Position = new Vector3 { X = x, Y = y, Z = z }
        };
    }
}
