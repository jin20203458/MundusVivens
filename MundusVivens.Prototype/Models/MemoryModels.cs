using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MundusVivens.Prototype.Models;

public class ChatMessage
{
    public string Role { get; set; } = "user"; // "user" | "model"
    public string Text { get; set; } = string.Empty;

    public ChatMessage() { }
    public ChatMessage(string role, string text)
    {
        Role = role;
        Text = text;
    }
}

public class ArchivedBelief
{
    public string Id { get; set; } = string.Empty; // agentId_beliefId
    public string AgentId { get; set; } = string.Empty;
    public Belief Belief { get; set; } = new();
}

public class MemoryBox
{
    private readonly object _lock = new();
    public List<ChatMessage> ActiveConversation { get; set; } = new();
    
    // 통합 믿음 보관소 (BeliefId -> Belief)
    public ConcurrentDictionary<string, Belief> Beliefs { get; set; } = new();

    public const int MaxTotalBeliefs = 40;
    public const int MaxCoreBeliefs = 5;

    // 🆕 도태(Eviction) 발생 시 데이터베이스 아카이브로 넘겨주기 위한 이벤트 훅 (직렬화 무시)
    [LiteDB.BsonIgnore]
    public Action<Belief>? OnBeliefEvicted { get; set; }

    public void AddOrUpdateBelief(Belief newBelief)
    {
        lock (_lock)
        {
            // 0. 중요도가 0.95 이상인 신념/기억은 Core로 자동 승격
            if (newBelief.Importance >= 0.95 && newBelief.Type != BeliefType.Core)
            {
                newBelief.Type = BeliefType.Core;
                Console.WriteLine($"[MemoryBox] 👑 Promoted belief '{newBelief.Content}' to Core Belief (Importance: {newBelief.Importance:F3})");
            }

            // 1. 이미 존재하는 정보인지 ID로 확인
            Beliefs.AddOrUpdate(newBelief.BeliefId, newBelief, (id, old) => {
                return newBelief;
            });

            // 2. Core 타입의 한도 및 밀어내기(강등) 처리
            var coreBeliefs = Beliefs.Values.Where(b => b.Type == BeliefType.Core).ToList();
            if (coreBeliefs.Count > MaxCoreBeliefs)
            {
                var demotedCore = coreBeliefs.OrderBy(b => b.Importance).FirstOrDefault();
                if (demotedCore != null)
                {
                    demotedCore.Type = BeliefType.Witnessed; // Core에서 Witnessed로 강등
                    demotedCore.AcquiredAt = DateTime.UtcNow; // 일반 기억이 되었으므로 획득 시간 기준 재조정
                }
            }

            // 3. 전체 메모리 예산 초과 시 도태(Eviction) 처리 (Core는 면역)
            while (Beliefs.Count > MaxTotalBeliefs)
            {
                var evictableCandidates = Beliefs.Values.Where(b => b.Type != BeliefType.Core).ToList();
                if (!evictableCandidates.Any()) break;

                var targetToEvict = evictableCandidates.OrderBy(b => b.Importance).FirstOrDefault();
                if (targetToEvict != null)
                {
                    if (Beliefs.TryRemove(targetToEvict.BeliefId, out var evicted))
                    {
                        OnBeliefEvicted?.Invoke(evicted);
                    }
                }
            }
        }
    }
}
