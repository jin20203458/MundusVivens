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
                c => {
                    if (c.Type == "Place")
                    {
                        return (c.Coordinates.X, c.Coordinates.Y, c.Coordinates.Z);
                    }
                    else
                    {
                        // 사각형(국가/도시)의 경우 중심점을 가상 좌표로 등록하여 하위 호환성 유지
                        return (
                            (c.MinBounds.X + c.MaxBounds.X) / 2f,
                            (c.MinBounds.Y + c.MaxBounds.Y) / 2f,
                            (c.MinBounds.Z + c.MaxBounds.Z) / 2f
                        );
                    }
                },
                StringComparer.OrdinalIgnoreCase
            );
            Console.WriteLine($"[Registry] Initialized with {_configs.Count} locations from configuration (including hierarchical regions).");
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
            return string.Join("\n", _configs.Select(c => $"- {c.SemanticName} ({c.Type})"));
        }
    }

    public static LocationConfig? GetConfig(string rawLocation)
    {
        if (string.IsNullOrWhiteSpace(rawLocation)) return null;

        lock (_lock)
        {
            foreach (var config in _configs)
            {
                if (config.SemanticName.Contains(rawLocation, StringComparison.OrdinalIgnoreCase) ||
                    rawLocation.Contains(config.SemanticName, StringComparison.OrdinalIgnoreCase))
                {
                    return config;
                }

                if (config.Aliases != null)
                {
                    foreach (var alias in config.Aliases)
                    {
                        if (rawLocation.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                            alias.Contains(rawLocation, StringComparison.OrdinalIgnoreCase))
                        {
                            return config;
                        }
                    }
                }
            }
            return null;
        }
    }

    public static List<string> GetLodLocationList(string currentPlaceName)
    {
        var coords = GetCoordinates(currentPlaceName);
        return GetLodLocationList(coords.X, coords.Z);
    }

    public static List<string> GetLodLocationList(float ax, float az)
    {
        lock (_lock)
        {
            // 1. 현재 에이전트 좌표를 포함하고 있는 영역(Region)들을 크기 순 정렬하여 추출
            var containingRegions = _configs
                .Where(c => c.Type != "Place" && 
                            ax >= c.MinBounds.X && ax <= c.MaxBounds.X && 
                            az >= c.MinBounds.Z && az <= c.MaxBounds.Z)
                .OrderBy(c => (c.MaxBounds.X - c.MinBounds.X) * (c.MaxBounds.Z - c.MinBounds.Z))
                .ToList();

            var currentCity = containingRegions.FirstOrDefault(r => r.Type == "City");
            var currentCountry = containingRegions.FirstOrDefault(r => r.Type == "Country");

            var result = new List<string>();

            foreach (var c in _configs)
            {
                if (c.Type == "Place")
                {
                    // 현재 위치한 도시 내부에 속한 세부 장소만 노출
                    if (currentCity != null)
                    {
                        if (IsInBox(c.Coordinates.X, c.Coordinates.Z, currentCity))
                        {
                            result.Add(c.SemanticName);
                        }
                    }
                    else if (currentCountry != null)
                    {
                        // 도시를 못 찾았을 경우 국가 내부 장소 노출
                        if (IsInBox(c.Coordinates.X, c.Coordinates.Z, currentCountry))
                        {
                            result.Add(c.SemanticName);
                        }
                    }
                    else
                    {
                        // 어떤 영역에도 속해있지 않은 경우 전역 장소 노출 (Fallback)
                        result.Add(c.SemanticName);
                    }
                }
                else if (c.Type == "City")
                {
                    if (c == currentCity) continue; // 현재 속한 도시는 스킵 (세부 장소들을 보임)

                    // 현재 국가 내부에 있는 다른 도시들만 노출
                    if (currentCountry != null)
                    {
                        float cx = (c.MinBounds.X + c.MaxBounds.X) / 2f;
                        float cz = (c.MinBounds.Z + c.MaxBounds.Z) / 2f;
                        if (IsInBox(cx, cz, currentCountry))
                        {
                            result.Add(c.SemanticName);
                        }
                    }
                }
                else if (c.Type == "Country")
                {
                    if (c == currentCountry) continue; // 현재 속한 국가는 스킵

                    // 다른 국가들은 국가명 자체를 노출 (원거리 마트료시카 껍데기)
                    result.Add(c.SemanticName);
                }
            }

            return result;
        }
    }

    private static bool IsInBox(float x, float z, LocationConfig box)
    {
        return x >= box.MinBounds.X && x <= box.MaxBounds.X &&
               z >= box.MinBounds.Z && z <= box.MaxBounds.Z;
    }

    public static (float X, float Y, float Z) GetCoordinates(string semanticName)
    {
        if (string.IsNullOrEmpty(semanticName)) return (0f, 0f, 0f);

        lock (_lock)
        {
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
        var config = GetConfig(rawLocation);
        return config?.SemanticName ?? GetAllSemanticNames().FirstOrDefault() ?? "Unknown";
    }

    private static readonly Dictionary<string, int> PredefinedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        { "발레리아 왕국", 1 },
        { "아르카디아 제국", 2 },
        { "루미스 마을", 10 },
        { "왕국 수도", 11 },
        { "제국 수도", 12 }
    };

    public static int GetDeterministicId(string name)
    {
        if (PredefinedIds.TryGetValue(name, out var id))
            return id;
        return Math.Abs(name.GetHashCode()) % 1000 + 100;
    }

    public static (int regionId, int territoryId) ResolveHierarchyIds(float x, float z)
    {
        int regionId = 0;
        int territoryId = 0;

        lock (_lock)
        {
            var containingCountry = _configs
                .FirstOrDefault(c => c.Type.Equals("Country", StringComparison.OrdinalIgnoreCase) && 
                                     x >= c.MinBounds.X && x <= c.MaxBounds.X && 
                                     z >= c.MinBounds.Z && z <= c.MaxBounds.Z);

            var containingCity = _configs
                .FirstOrDefault(c => c.Type.Equals("City", StringComparison.OrdinalIgnoreCase) && 
                                     x >= c.MinBounds.X && x <= c.MaxBounds.X && 
                                     z >= c.MinBounds.Z && z <= c.MaxBounds.Z);

            if (containingCountry != null)
                regionId = GetDeterministicId(containingCountry.SemanticName);
            
            if (containingCity != null)
                territoryId = GetDeterministicId(containingCity.SemanticName);
        }

        return (regionId, territoryId);
    }

    public static LocationInfo CreateLocationInfo(string name, float x, float y, float z)
    {
        var config = GetConfig(name);
        var protoType = ProtoLocationType.LocationTypeUnspecified;

        if (config != null)
        {
            protoType = config.Type.ToLower() switch
            {
                "country" => ProtoLocationType.LocationTypeCountry,
                "city" => ProtoLocationType.LocationTypeCity,
                "place" => ProtoLocationType.LocationTypePlace,
                _ => ProtoLocationType.LocationTypeUnspecified
            };
            
            // If it's a place, refine type by semantic name matching
            if (protoType == ProtoLocationType.LocationTypePlace)
            {
                var lowerName = config.SemanticName.ToLower();
                if (lowerName.Contains("tavern") || lowerName.Contains("술집"))
                    protoType = ProtoLocationType.LocationTypeTavern;
                else if (lowerName.Contains("market") || lowerName.Contains("시장"))
                    protoType = ProtoLocationType.LocationTypeMarket;
                else if (lowerName.Contains("square") || lowerName.Contains("광장"))
                    protoType = ProtoLocationType.LocationTypeSquare;
                else if (lowerName.Contains("church") || lowerName.Contains("성당"))
                    protoType = ProtoLocationType.LocationTypeChurch;
                else if (lowerName.Contains("forge") || lowerName.Contains("대장간"))
                    protoType = ProtoLocationType.LocationTypeForge;
                else if (lowerName.Contains("manor") || lowerName.Contains("저택"))
                    protoType = ProtoLocationType.LocationTypeManor;
                else if (lowerName.Contains("wilderness") || lowerName.Contains("황무지"))
                    protoType = ProtoLocationType.LocationTypeWilderness;
                else
                    protoType = ProtoLocationType.LocationTypeResidential;
            }
        }
        else
        {
            var lowerName = (name ?? "").ToLower();
            if (lowerName.Contains("tavern") || lowerName.Contains("술집"))
                protoType = ProtoLocationType.LocationTypeTavern;
            else if (lowerName.Contains("market") || lowerName.Contains("시장"))
                protoType = ProtoLocationType.LocationTypeMarket;
            else if (lowerName.Contains("square") || lowerName.Contains("광장"))
                protoType = ProtoLocationType.LocationTypeSquare;
            else if (lowerName.Contains("church") || lowerName.Contains("성당"))
                protoType = ProtoLocationType.LocationTypeChurch;
            else if (lowerName.Contains("forge") || lowerName.Contains("대장간"))
                protoType = ProtoLocationType.LocationTypeForge;
            else if (lowerName.Contains("manor") || lowerName.Contains("저택"))
                protoType = ProtoLocationType.LocationTypeManor;
            else if (lowerName.Contains("wilderness") || lowerName.Contains("황무지"))
                protoType = ProtoLocationType.LocationTypeWilderness;
            else
                protoType = ProtoLocationType.LocationTypeResidential;
        }

        var (regionId, territoryId) = ResolveHierarchyIds(x, z);

        return new LocationInfo
        {
            Name = name ?? "Unknown",
            Position = new Vector3 { X = x, Y = y, Z = z },
            Type = protoType,
            RegionId = (uint)regionId,
            TerritoryId = (uint)territoryId
        };
    }

    public static LocationInfo CreateLocationInfo(string name)
    {
        var (x, y, z) = GetCoordinates(name);
        return CreateLocationInfo(name, x, y, z);
    }

    public static List<LocationConfig> GetConfigs()
    {
        lock (_lock)
        {
            return _configs.ToList();
        }
    }

    public static (float X, float Y, float Z) GetTargetCoordinate(string fromLoc, string toLoc)
    {
        var configA = GetConfig(fromLoc);
        var configB = GetConfig(toLoc);
        if (configB == null) return (0f, 0f, 0f);

        if (configB.Type == "Place")
        {
            return (configB.Coordinates.X, configB.Coordinates.Y, configB.Coordinates.Z);
        }

        // AABB(국가/도시) 타겟인 경우 출발지 기준 테두리 경계선 좌표 Clamping 계산
        float ax = 0f, ay = 0f, az = 0f;
        if (configA != null)
        {
            if (configA.Type == "Place")
            {
                ax = configA.Coordinates.X;
                ay = configA.Coordinates.Y;
                az = configA.Coordinates.Z;
            }
            else
            {
                ax = (configA.MinBounds.X + configA.MaxBounds.X) / 2f;
                ay = (configA.MinBounds.Y + configA.MaxBounds.Y) / 2f;
                az = (configA.MinBounds.Z + configA.MaxBounds.Z) / 2f;
            }
        }

        float bx = Math.Clamp(ax, configB.MinBounds.X, configB.MaxBounds.X);
        float by = Math.Clamp(ay, configB.MinBounds.Y, configB.MaxBounds.Y);
        float bz = Math.Clamp(az, configB.MinBounds.Z, configB.MaxBounds.Z);
        return (bx, by, bz);
    }

    public static int GetTravelTimeHours(string locA, string locB)
    {
        if (string.IsNullOrWhiteSpace(locA) || string.IsNullOrWhiteSpace(locB)) return 0;
        
        string parsedA = ParseLocation(locA);
        string parsedB = ParseLocation(locB);
        if (string.Equals(parsedA, parsedB, StringComparison.OrdinalIgnoreCase)) return 0;

        var configA = GetConfig(parsedA);
        var configB = GetConfig(parsedB);
        if (configA == null || configB == null) return 0;

        float ax, ay, az;
        if (configA.Type == "Place")
        {
            ax = configA.Coordinates.X;
            ay = configA.Coordinates.Y;
            az = configA.Coordinates.Z;
        }
        else
        {
            ax = (configA.MinBounds.X + configA.MaxBounds.X) / 2f;
            ay = (configA.MinBounds.Y + configA.MaxBounds.Y) / 2f;
            az = (configA.MinBounds.Z + configA.MaxBounds.Z) / 2f;
        }

        // B가 사각 영역인 경우 가장 가까운 경계선(Clamped)을 찾아 거리 계산
        float bx, by, bz;
        if (configB.Type == "Place")
        {
            bx = configB.Coordinates.X;
            by = configB.Coordinates.Y;
            bz = configB.Coordinates.Z;
        }
        else
        {
            bx = Math.Clamp(ax, configB.MinBounds.X, configB.MaxBounds.X);
            by = Math.Clamp(ay, configB.MinBounds.Y, configB.MaxBounds.Y);
            bz = Math.Clamp(az, configB.MinBounds.Z, configB.MaxBounds.Z);
        }

        double dx = ax - bx;
        double dy = ay - by;
        double dz = az - bz;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        // NPC speed = 2.0 m/s
        // 1 game hour = 200 physics ticks = 10 real-world seconds
        // Therefore, 1 game hour travel distance = 2.0 m/s * 10s = 20.0 meters.
        double hours = distance / 20.0;
        return (int)Math.Max(1, Math.Round(hours));
    }

    public static int GetTravelTimeHoursFromCoord(float ax, float ay, float az, string targetLoc)
    {
        if (string.IsNullOrWhiteSpace(targetLoc)) return 0;
        
        string parsedB = ParseLocation(targetLoc);
        var configB = GetConfig(parsedB);
        if (configB == null) return 0;

        float bx, by, bz;
        if (configB.Type == "Place")
        {
            bx = configB.Coordinates.X;
            by = configB.Coordinates.Y;
            bz = configB.Coordinates.Z;
        }
        else
        {
            bx = Math.Clamp(ax, configB.MinBounds.X, configB.MaxBounds.X);
            by = Math.Clamp(ay, configB.MinBounds.Y, configB.MaxBounds.Y);
            bz = Math.Clamp(az, configB.MinBounds.Z, configB.MaxBounds.Z);
        }

        double dx = ax - bx;
        double dy = ay - by;
        double dz = az - bz;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        // 도착 기준 거리: 테두리 경계선과 1.5미터 이하
        if (distance < 1.5) return 0;

        double hours = distance / 20.0;
        return (int)Math.Max(1, Math.Round(hours));
    }
}

