using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MundusVivens.Prototype.Models;

namespace MundusVivens.Prototype.Services;

public class StaticDataLoader
{
    private readonly string _worldConfigPath;

    public StaticDataLoader()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectDataDir = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        var outputDataDir = Path.Combine(baseDir, "Data");

        var selectedDataDir = Directory.Exists(projectDataDir) ? projectDataDir : outputDataDir;
        _worldConfigPath = Path.Combine(selectedDataDir, "World", "world_config.json");
    }

    public ConcurrentDictionary<string, AgentInstance> LoadInitialAgents()
    {
        var agents = new ConcurrentDictionary<string, AgentInstance>();

        if (!File.Exists(_worldConfigPath))
        {
            Console.WriteLine($"[Warning] World config file not found at: {_worldConfigPath}");
            return agents;
        }

        try
        {
            var json = File.ReadAllText(_worldConfigPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 1. Locations 파싱 및 LocationCoordinateRegistry 초기화
            if (root.TryGetProperty("Locations", out var locationsProp))
            {
                var locations = JsonSerializer.Deserialize<List<LocationConfig>>(locationsProp.GetRawText(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (locations != null)
                {
                    LocationCoordinateRegistry.Initialize(locations);
                }
            }

            // 2. Agents 파싱
            if (root.TryGetProperty("Agents", out var agentsProp) && agentsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var agentElem in agentsProp.EnumerateArray())
                {
                    string agentJson = agentElem.GetRawText();
                    
                    // Faction 정보 수동 추출
                    string faction = string.Empty;
                    if (agentElem.TryGetProperty("Persona", out var personaProp) &&
                        personaProp.TryGetProperty("Faction", out var factionProp))
                    {
                        faction = factionProp.GetString() ?? string.Empty;
                    }

                    var agent = JsonSerializer.Deserialize<AgentInstance>(agentJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (agent != null && !string.IsNullOrEmpty(agent.AgentId))
                    {
                        agent.RelationshipMap ??= new ConcurrentDictionary<string, Relationship>();
                        agent.MemoryBox ??= new MemoryBox();
                        agent.MemoryBox.Beliefs ??= new ConcurrentDictionary<string, Belief>();
                        agent.MemoryBox.ActiveConversation ??= new List<ChatMessage>();

                        if (!string.IsNullOrWhiteSpace(faction))
                        {
                            var factionBelief = new Belief
                            {
                                BeliefId = "belief_faction_identity",
                                SubjectId = agent.AgentId,
                                Content = $"소속 및 정체성: 나는 {faction}에 소속되어 있으며, 이들의 이익과 가치관을 대변한다.",
                                Type = BeliefType.Core,
                                Confidence = 1.0,
                                Salience = 1.0,
                                EmotionalCharge = 0.5,
                                SourceAgentId = "SystemInitialSeed",
                                AcquiredAt = DateTime.UtcNow
                            };
                            agent.MemoryBox.AddOrUpdateBelief(factionBelief);
                            Console.WriteLine($"[DataLoader] Injected Faction Core Belief for {agent.Persona.Name}: {faction}");
                        }

                        agents[agent.AgentId] = agent;
                        Console.WriteLine($"[DataLoader] Loaded Character: {agent.Persona.Name} ({agent.AgentId})");
                    }
                }
            }

            // 3. Relationships 파싱
            if (root.TryGetProperty("Relationships", out var relsProp) && relsProp.ValueKind == JsonValueKind.Array)
            {
                var relationships = JsonSerializer.Deserialize<List<RelationshipDto>>(relsProp.GetRawText(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (relationships != null)
                {
                    foreach (var rel in relationships)
                    {
                        if (agents.TryGetValue(rel.SourceAgentId, out var sourceAgent))
                        {
                            sourceAgent.RelationshipMap[rel.TargetAgentId] = new Relationship
                            {
                                TargetAgentId = rel.TargetAgentId,
                                Liking = rel.Liking,
                                Trust = rel.Trust
                            };
                        }
                    }
                }
            }

            // 4. SeedGossips 파싱
            if (root.TryGetProperty("SeedGossips", out var gossipsProp) && gossipsProp.ValueKind == JsonValueKind.Array)
            {
                var seedGossips = JsonSerializer.Deserialize<List<SeedGossipDto>>(gossipsProp.GetRawText(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (seedGossips != null)
                {
                    foreach (var seed in seedGossips)
                    {
                        if (agents.TryGetValue(seed.HolderAgentId, out var holderAgent))
                        {
                            var initialBelief = new Belief
                            {
                                BeliefId = seed.Gossip.GossipId,
                                SubjectId = seed.Gossip.Subject,
                                Content = seed.Gossip.Content,
                                Type = BeliefType.Heard,
                                Confidence = seed.SubjectiveBelief,
                                Salience = 1.0,
                                EmotionalCharge = 0.5,
                                SourceAgentId = seed.Gossip.SourceAgentId,
                                MutationCount = seed.Gossip.MutationCount,
                                AcquiredAt = DateTime.UtcNow
                            };
                            holderAgent.MemoryBox.AddOrUpdateBelief(initialBelief);
                            Console.WriteLine($"[DataLoader] Seeded Belief '{seed.Gossip.GossipId}' to {holderAgent.Persona.Name}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to load master world config: {ex.Message}");
        }

        return agents;
    }

    private class RelationshipDto
    {
        public string SourceAgentId { get; set; } = string.Empty;
        public string TargetAgentId { get; set; } = string.Empty;
        public int Liking { get; set; }
        public int Trust { get; set; }
    }

    private class SeedGossipDto
    {
        public GossipItemDto Gossip { get; set; } = new();
        public string HolderAgentId { get; set; } = string.Empty;
        public double SubjectiveBelief { get; set; }
    }

    private class GossipItemDto
    {
        public string GossipId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string SourceAgentId { get; set; } = string.Empty;
        public int BaseCredibility { get; set; }
        public int MutationCount { get; set; }
    }
}
