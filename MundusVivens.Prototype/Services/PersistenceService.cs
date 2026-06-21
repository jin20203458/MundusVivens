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

        // 2. ConcurrentDictionary<string, KnownGossip> 매핑 설정
        mapper.RegisterType<ConcurrentDictionary<string, KnownGossip>>(
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
                var dict = new ConcurrentDictionary<string, KnownGossip>();
                if (bson.IsDocument)
                {
                    foreach (var kv in bson.AsDocument)
                    {
                        var val = mapper.ToObject<KnownGossip>(kv.Value.AsDocument);
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

        // 4. ConcurrentQueue<Episode> 매핑 설정
        mapper.RegisterType<ConcurrentQueue<Episode>>(
            queue =>
            {
                var arr = new BsonArray();
                foreach (var ep in queue)
                {
                    arr.Add(mapper.ToDocument(ep));
                }
                return arr;
            },
            bson =>
            {
                var queue = new ConcurrentQueue<Episode>();
                if (bson.IsArray)
                {
                    foreach (var val in bson.AsArray)
                    {
                        var ep = mapper.ToObject<Episode>(val.AsDocument);
                        queue.Enqueue(ep);
                    }
                }
                return queue;
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
