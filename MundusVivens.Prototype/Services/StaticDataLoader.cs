using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MundusVivens.Prototype.Models;

namespace MundusVivens.Prototype.Services;

public class StaticDataLoader
{
    private readonly string _charactersPath;
    private readonly string _worldConfigPath;

    public StaticDataLoader()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        
        // 데이터 파일 경로 우선순위:
        // 1. 실행파일 디렉토리 하위의 Data (빌드 출력 복사본)
        // 2. 프로젝트 소스 디렉토리 하위의 Data (개발 환경)
        var projectDataDir = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        var outputDataDir = Path.Combine(baseDir, "Data");

        var selectedDataDir = Directory.Exists(projectDataDir) ? projectDataDir : outputDataDir;

        _charactersPath = Path.Combine(selectedDataDir, "Characters");
        _worldConfigPath = Path.Combine(selectedDataDir, "World", "initial_world.json");
    }

    public ConcurrentDictionary<string, AgentInstance> LoadInitialAgents()
    {
        var agents = new ConcurrentDictionary<string, AgentInstance>();

        if (!Directory.Exists(_charactersPath))
        {
            Console.WriteLine($"[Warning] Characters directory not found at: {_charactersPath}");
            return agents;
        }

        // 1. 캐릭터 개별 파일 로드
        var charFiles = Directory.GetFiles(_charactersPath, "*.json");
        foreach (var file in charFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                var agent = JsonSerializer.Deserialize<AgentInstance>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (agent != null && !string.IsNullOrEmpty(agent.AgentId))
                {
                    // Dictionary 초기화 보장
                    agent.KnownGossips ??= new ConcurrentDictionary<string, KnownGossip>();
                    agent.RelationshipMap ??= new ConcurrentDictionary<string, Relationship>();
                    agent.MemoryBox ??= new MemoryBox();
                    agent.MemoryBox.CoreMemories ??= new List<CoreFact>();
                    agent.MemoryBox.ActiveConversation ??= new List<ChatMessage>();
                    agent.MemoryBox.EpisodicMemories ??= new ConcurrentQueue<Episode>();

                    agents[agent.AgentId] = agent;
                    Console.WriteLine($"[DataLoader] Loaded Character: {agent.Persona.Name} ({agent.AgentId})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load character file '{file}': {ex.Message}");
            }
        }

        // 2. 초기 월드 설정 (관계, 초기 소문) 로드
        if (File.Exists(_worldConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_worldConfigPath);
                var worldConfig = JsonSerializer.Deserialize<WorldConfigDto>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (worldConfig != null)
                {
                    // 관계 세팅
                    if (worldConfig.Relationships != null)
                    {
                        foreach (var rel in worldConfig.Relationships)
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

                    // 초기 소문 세팅
                    if (worldConfig.SeedGossips != null)
                    {
                        foreach (var seed in worldConfig.SeedGossips)
                        {
                            if (agents.TryGetValue(seed.HolderAgentId, out var holderAgent))
                            {
                                holderAgent.KnownGossips[seed.Gossip.GossipId] = new KnownGossip
                                {
                                    Gossip = seed.Gossip,
                                    SubjectiveBelief = seed.SubjectiveBelief,
                                    HasSharedWithOthers = false
                                };
                                Console.WriteLine($"[DataLoader] Seeded Gossip '{seed.Gossip.GossipId}' to {holderAgent.Persona.Name}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load world config: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[Warning] World config file not found at: {_worldConfigPath}");
        }

        return agents;
    }

    // JSON 바인딩을 위한 DTO 정의
    private class WorldConfigDto
    {
        public List<RelationshipDto>? Relationships { get; set; }
        public List<SeedGossipDto>? SeedGossips { get; set; }
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
        public GossipItem Gossip { get; set; } = new();
        public string HolderAgentId { get; set; } = string.Empty;
        public double SubjectiveBelief { get; set; }
    }
}
