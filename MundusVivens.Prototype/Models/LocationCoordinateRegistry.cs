using System;
using System.Collections.Generic;
using System.Linq;
using MundusVivens.Prototype.Protos;

namespace MundusVivens.Prototype.Models;

public static class LocationCoordinateRegistry
{
    private static readonly object _lock = new();
    private static Dictionary<string, (float X, float Y, float Z)> _semanticToCoords = new(StringComparer.OrdinalIgnoreCase);
    private static List<LocationConfig> _configs = new();

    public static void Initialize(IEnumerable<LocationConfig> configs)
    {
        lock (_lock)
        {
            _configs = configs.ToList();
            _semanticToCoords = _configs.ToDictionary(
                c => c.SemanticName,
                c => (c.Coordinates.X, c.Coordinates.Y, c.Coordinates.Z),
                StringComparer.OrdinalIgnoreCase
            );
            Console.WriteLine($"[Registry] Initialized with {_configs.Count} locations from configuration.");
        }
    }

    public static List<string> GetAllSemanticNames()
    {
        lock (_lock)
        {
            return _configs.Select(c => c.SemanticName).ToList();
        }
    }

    public static string GetPromptLocationList()
    {
        lock (_lock)
        {
            return string.Join("\n", _configs.Select(c => $"- {c.SemanticName}"));
        }
    }

    public static (float X, float Y, float Z) GetCoordinates(string semanticName)
    {
        if (string.IsNullOrEmpty(semanticName)) return (0f, 0f, 0f);

        lock (_lock)
        {
            // Direct/contains check on keys
            foreach (var kv in _semanticToCoords)
            {
                if (semanticName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) || 
                    kv.Key.Contains(semanticName, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }
        }
        return (0f, 0f, 0f);
    }

    public static string ParseLocation(string rawLocation)
    {
        if (string.IsNullOrWhiteSpace(rawLocation)) return GetAllSemanticNames().FirstOrDefault() ?? "Unknown";

        lock (_lock)
        {
            foreach (var config in _configs)
            {
                if (config.SemanticName.Contains(rawLocation, StringComparison.OrdinalIgnoreCase) ||
                    rawLocation.Contains(config.SemanticName, StringComparison.OrdinalIgnoreCase))
                {
                    return config.SemanticName;
                }

                if (config.Aliases != null)
                {
                    foreach (var alias in config.Aliases)
                    {
                        if (rawLocation.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                            alias.Contains(rawLocation, StringComparison.OrdinalIgnoreCase))
                        {
                            return config.SemanticName;
                        }
                    }
                }
            }
            
            return GetAllSemanticNames().FirstOrDefault() ?? "Unknown";
        }
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

    public static List<LocationConfig> GetConfigs()
    {
        lock (_lock)
        {
            return _configs.ToList();
        }
    }
}
