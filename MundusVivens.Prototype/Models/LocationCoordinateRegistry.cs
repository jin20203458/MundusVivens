using System;
using System.Collections.Generic;
using MundusVivens.Prototype.Protos;

namespace MundusVivens.Prototype.Models;

public static class LocationCoordinateRegistry
{
    private static readonly Dictionary<string, (float X, float Y, float Z)> SemanticToCoords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "영주 저택 (Manor)", (85.0f, 0.0f, 90.0f) },
        { "성당 (Church)", (20.0f, 0.0f, 80.0f) },
        { "경비 초소 (Guard Post)", (15.0f, 0.0f, 20.0f) },
        { "연금술 공방 (Alchemy Lab)", (80.0f, 0.0f, 70.0f) },
        { "광장 (Square)", (50.0f, 0.0f, 50.0f) },
        { "마을 광장 (Square)", (50.0f, 0.0f, 50.0f) },
        { "대장간 (Forge)", (70.0f, 0.0f, 30.0f) },
        { "뒷골목 (Back Alley)", (15.0f, 0.0f, 50.0f) },
        { "술집 (Tavern)", (30.0f, 0.0f, 40.0f) },
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
