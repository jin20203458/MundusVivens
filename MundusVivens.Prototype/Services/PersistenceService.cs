using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using MundusVivens.Prototype.Models;

namespace MundusVivens.Prototype.Services;

public interface IPersistenceService
{
    ConcurrentDictionary<string, AgentInstance> LoadAllAgents();
    void UpsertAgent(AgentInstance agent);
    void ResetDatabase(IEnumerable<AgentInstance> initialAgents);
    void ArchiveBelief(string agentId, Belief belief);
    List<Belief> RecallBeliefs(string agentId, string? location, string? targetAgentId, List<string>? keywords, int limit = 5);
}

public class PersistenceService : IPersistenceService, IDisposable
{
    private readonly string _dbPath;
    private LiteDatabase? _database;
    private readonly object _dbLock = new();

    public PersistenceService()
    {
        var saveDir = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Save");
        if (!Directory.Exists(saveDir))
        {
            Directory.CreateDirectory(saveDir);
        }

        _dbPath = Path.Combine(saveDir, "GameData.db");
        
        ConfigureBsonMapper();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        lock (_dbLock)
        {
            try
            {
                _database = new LiteDatabase(_dbPath);
                Console.WriteLine($"[Persistence] Database initialized at: {_dbPath}");

                // AgentId 인덱스 생성 (RecallBeliefs 성능 최적화)
                var col = _database.GetCollection<ArchivedBelief>("cold_archive");
                col.EnsureIndex(x => x.AgentId);
                Console.WriteLine("[Persistence] cold_archive에 AgentId 인덱스 적용 완료.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persistence Error] Failed to initialize LiteDB: {ex.Message}");
                throw;
            }
        }
    }

    private void ConfigureBsonMapper()
    {
        var mapper = BsonMapper.Global;

        // 1. AgentInstance의 기본 키(Primary Key) 설정
        mapper.Entity<AgentInstance>()
            .Id(x => x.AgentId);

        // 1-2. ArchivedBelief의 기본 키 설정
        mapper.Entity<ArchivedBelief>()
            .Id(x => x.Id);

        // 2. ConcurrentDictionary<string, Belief> 매핑 설정
        mapper.RegisterType<ConcurrentDictionary<string, Belief>>(
            dict =>
            {
                var doc = new BsonDocument();
                foreach (var kv in dict)
                {
                    doc[kv.Key] = mapper.ToDocument(kv.Value);
                }
                return doc;
            },
            bson =>
            {
                var dict = new ConcurrentDictionary<string, Belief>();
                if (bson.IsDocument)
                {
                    foreach (var kv in bson.AsDocument)
                    {
                        var val = mapper.ToObject<Belief>(kv.Value.AsDocument);
                        dict[kv.Key] = val;
                    }
                }
                return dict;
            }
        );

        // 3. ConcurrentDictionary<string, Relationship> 매핑 설정
        mapper.RegisterType<ConcurrentDictionary<string, Relationship>>(
            dict =>
            {
                var doc = new BsonDocument();
                foreach (var kv in dict)
                {
                    doc[kv.Key] = mapper.ToDocument(kv.Value);
                }
                return doc;
            },
            bson =>
            {
                var dict = new ConcurrentDictionary<string, Relationship>();
                if (bson.IsDocument)
                {
                    foreach (var kv in bson.AsDocument)
                    {
                        var val = mapper.ToObject<Relationship>(kv.Value.AsDocument);
                        dict[kv.Key] = val;
                    }
                }
                return dict;
            }
        );
    }

    public ConcurrentDictionary<string, AgentInstance> LoadAllAgents()
    {
        lock (_dbLock)
        {
            if (_database == null) InitializeDatabase();

            var dict = new ConcurrentDictionary<string, AgentInstance>();
            try
            {
                var col = _database!.GetCollection<AgentInstance>("agents");
                var list = col.FindAll().ToList();

                foreach (var agent in list)
                {
                    agent.MemoryBox.OnBeliefEvicted = evicted => ArchiveBelief(agent.AgentId, evicted);
                    dict[agent.AgentId] = agent;
                }
                
                Console.WriteLine($"[Persistence] Loaded {dict.Count} agents from database.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persistence Error] Failed to load agents: {ex.Message}");
            }
            return dict;
        }
    }

    public void UpsertAgent(AgentInstance agent)
    {
        // 락을 걸기 전 비동기로 수행될 수도 있으므로 락 획득 범위 최소화
        lock (_dbLock)
        {
            if (_database == null) InitializeDatabase();

            try
            {
                var col = _database!.GetCollection<AgentInstance>("agents");
                col.Upsert(agent);
                Console.WriteLine($"[Persistence] Async Saved Agent Status: {agent.Persona.Name} ({agent.AgentId})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persistence Error] Failed to upsert agent '{agent.AgentId}': {ex.Message}");
            }
        }
    }

    public void ResetDatabase(IEnumerable<AgentInstance> initialAgents)
    {
        lock (_dbLock)
        {
            if (_database == null) InitializeDatabase();

            try
            {
                _database!.DropCollection("agents");
                var col = _database.GetCollection<AgentInstance>("agents");
                col.InsertBulk(initialAgents);
                
                // LiteDB 강제 디스크 플러시/체크포인트
                _database.Rebuild();
                Console.WriteLine("[Persistence] Database has been reset to initial static configurations.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persistence Error] Failed to reset database: {ex.Message}");
            }
        }
    }

    public void ArchiveBelief(string agentId, Belief belief)
    {
        lock (_dbLock)
        {
            if (_database == null) InitializeDatabase();
            try
            {
                var col = _database!.GetCollection<ArchivedBelief>("cold_archive");
                var archived = new ArchivedBelief
                {
                    Id = $"{agentId}_{belief.BeliefId}",
                    AgentId = agentId,
                    Belief = belief
                };
                col.Upsert(archived);
                
                int currentArchiveCount = col.Count(x => x.AgentId == agentId);
                Console.WriteLine($"📥 [기억 Eviction 발생] 에이전트: {agentId} | 핫 메모리 쇠퇴 ➔ Cold Archive 이관");
                Console.WriteLine($"   - 이관 기억 내용: \"{belief.Content}\" (중요도: {belief.Importance})");
                Console.WriteLine($"   - Cold 데이터베이스 아카이브 크기: {currentArchiveCount}개");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persistence Error] Failed to archive belief: {ex.Message}");
            }
        }
    }

    public List<Belief> RecallBeliefs(string agentId, string? location, string? targetAgentId, List<string>? keywords, int limit = 5)
    {
        lock (_dbLock)
        {
            if (_database == null) InitializeDatabase();
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var col = _database!.GetCollection<ArchivedBelief>("cold_archive");
                
                // 1. 해당 에이전트의 보관된 기억 쿼리 (인덱스 활용 lazy cursor)
                var query = col.Find(x => x.AgentId == agentId);
                int totalScanned = 0;

                // 2. 가중치 매칭 스코어 계산 (지연 연산)
                var scored = query.Select(ab =>
                {
                    totalScanned++;
                    double score = 0.0;

                    // 대상(인물) 매칭 가중치
                    if (!string.IsNullOrEmpty(targetAgentId) && ab.Belief.SubjectId == targetAgentId)
                    {
                        score += 5.0; // 강한 인물 연상
                    }

                    // 장소 매칭 가중치 (기억 내용이나 대상에 장소 이름 포함 확인)
                    if (!string.IsNullOrEmpty(location))
                    {
                        if (ab.Belief.Content.Contains(location, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 3.0; // 장소 연상
                        }
                    }

                    // 키워드 매칭 가중치
                    if (keywords != null && keywords.Any())
                    {
                        foreach (var kw in keywords)
                        {
                            if (ab.Belief.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            {
                                score += 2.0;
                            }
                        }
                    }

                    // 중요도 가중치
                    score += ab.Belief.Importance * 2.0;

                    // 최신성(Recency) 가중치 (경과 시간 역산)
                    var timeSpan = DateTime.UtcNow - ab.Belief.AcquiredAt;
                    double recencyScore = Math.Max(0, 1.0 - (timeSpan.TotalHours / 72.0)); // 72시간 기준 선형 감쇠
                    score += recencyScore * 1.5;

                    return new { Archived = ab, Score = score };
                })
                .Where(x => x.Score > 0.0)
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => x.Archived.Belief)
                .ToList();

                stopwatch.Stop();
                Console.WriteLine($"⏱️ [기억 회상 프로파일러] Agent: {agentId} | 스캔한 기억 개수: {totalScanned}개 | 최종 채택: {scored.Count}개 | 소요 시간: {stopwatch.ElapsedMilliseconds} ms");

                return scored;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persistence Error] Failed to recall beliefs: {ex.Message}");
                return new List<Belief>();
            }
        }
    }

    public void Dispose()
    {
        lock (_dbLock)
        {
            if (_database != null)
            {
                _database.Dispose();
                _database = null;
                Console.WriteLine("[Persistence] Database connection closed gracefully.");
            }
        }
    }
}
