using MundusVivens.Prototype.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IBeliefEngine
{
    int CurrentTick { get; set; }
    Belief? SelectBeliefToShare(AgentInstance speaker, AgentInstance listener, IReadOnlyList<Belief> topKCandidates);
    Task ProcessBeliefSharingAsync(AgentInstance speaker, AgentInstance listener, Belief originalBelief, string sharedContent);
    void DecayBeliefs(AgentInstance agent, int currentTick);
    void PropagateCausalCascade(AgentInstance agent, string parentBeliefId);
    Task ProcessCombatEventAsync(AgentInstance attacker, AgentInstance victim, float damage, string weapon);
}

public class BeliefEngine : IBeliefEngine
{
    public int CurrentTick { get; set; } = 0;
    private readonly IGeminiApiService _geminiApi;
    private readonly IEmbeddingCache _embeddingCache;
    private readonly Random _random = new();

    private readonly Dictionary<string, int> _lastDecayedTicks = new();

    public BeliefEngine(IGeminiApiService geminiApi, IEmbeddingCache embeddingCache)
    {
        _geminiApi = geminiApi;
        _embeddingCache = embeddingCache;
    }

    public Belief? SelectBeliefToShare(AgentInstance speaker, AgentInstance listener, IReadOnlyList<Belief> topKCandidates)
    {
        var candidateBeliefs = topKCandidates
            .Where(b => b.SubjectId != listener.AgentId)
            .Where(b => b.Type != BeliefType.Core)
            .Where(b => b.Confidence >= 0.2)
            .Where(b => !listener.MemoryBox.Beliefs.ContainsKey(b.BeliefId) || 
                         b.Confidence > listener.MemoryBox.Beliefs[b.BeliefId].Confidence)
            .Where(b => !b.SharedWith.Contains(listener.AgentId))
            .ToList();

        if (!candidateBeliefs.Any()) return null;

        double shareChance = speaker.Persona.Extroversion;
        if (speaker.RelationshipMap.TryGetValue(listener.AgentId, out var rel))
        {
            shareChance += (rel.Liking + rel.Trust) / 400.0;
        }

        shareChance = Math.Clamp(shareChance, 0.1, 0.95);

        if (speaker.Persona.Extroversion < 0.8 && _random.NextDouble() > shareChance)
        {
            return null;
        }

        return candidateBeliefs.OrderByDescending(b => b.Salience).FirstOrDefault();
    }

    private async Task<float[]> GetOrComputeEmbeddingHelperAsync(string text)
    {
        return await _embeddingCache.GetOrComputeEmbeddingAsync(text, async t =>
        {
            return await _geminiApi.GetEmbeddingAsync(t);
        });
    }

    public async Task ProcessBeliefSharingAsync(AgentInstance speaker, AgentInstance listener, Belief originalBelief, string sharedContent)
    {
        originalBelief.SharedWith.Add(listener.AgentId);
        originalBelief.Salience = 1.0;

        float[] sharedEmbedding = await GetOrComputeEmbeddingHelperAsync(sharedContent);

        if (originalBelief.ContentEmbedding == null)
        {
            originalBelief.ContentEmbedding = await GetOrComputeEmbeddingHelperAsync(originalBelief.Content);
        }

        List<string> speakerPath = new List<string>(originalBelief.PropagationPath);

        if (listener.MemoryBox.Beliefs.TryGetValue(originalBelief.BeliefId, out var directBelief))
        {
            directBelief.AcquiredAt = DateTime.UtcNow;
            directBelief.Salience = 1.0;

            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                double impact = (rel.Trust / 100.0) * 0.2;
                directBelief.Confidence = Math.Clamp(directBelief.Confidence + impact, 0.0, 1.0);
            }

            bool isMutated = !originalBelief.Content.Trim().Equals(sharedContent.Trim(), StringComparison.OrdinalIgnoreCase);
            if (isMutated && originalBelief.MutationCount + 1 > directBelief.MutationCount)
            {
                directBelief.Content = sharedContent;
                directBelief.ContentEmbedding = sharedEmbedding;
                directBelief.MutationCount = originalBelief.MutationCount + 1;
                Console.WriteLine($"[BeliefEngine] 🧬 [정보 업데이트] {listener.Persona.Name}이(가) 더 와전된 내용을 습득했습니다 -> \"{sharedContent}\"");
            }
            return;
        }

        Belief? matchedBelief = null;
        double maxSimilarity = 0;

        var subjectCandidates = listener.MemoryBox.Beliefs.Values
            .Where(b => b.SubjectId == originalBelief.SubjectId && b.Type != BeliefType.Core)
            .ToList();

        foreach (var candidate in subjectCandidates)
        {
            if (candidate.ContentEmbedding == null)
            {
                candidate.ContentEmbedding = await GetOrComputeEmbeddingHelperAsync(candidate.Content);
            }

            double similarity = EmbeddingCache.CosineSimilarity(sharedEmbedding, candidate.ContentEmbedding);
            if (similarity > maxSimilarity)
            {
                maxSimilarity = similarity;
                matchedBelief = candidate;
            }
        }

        bool isMeaningSame = maxSimilarity >= 0.82;

        if (isMeaningSame && matchedBelief != null)
        {
            matchedBelief.AcquiredAt = DateTime.UtcNow;
            matchedBelief.Salience = 1.0;

            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                double impact = (rel.Trust / 100.0) * 0.2;
                matchedBelief.Confidence = Math.Clamp(matchedBelief.Confidence + impact, 0.0, 1.0);
            }

            bool isTextMutated = !matchedBelief.Content.Trim().Equals(sharedContent.Trim(), StringComparison.OrdinalIgnoreCase);
            if (isTextMutated)
            {
                string oldContent = matchedBelief.Content;
                matchedBelief.Content = sharedContent;
                matchedBelief.ContentEmbedding = sharedEmbedding;
                matchedBelief.MutationCount = Math.Max(matchedBelief.MutationCount, originalBelief.MutationCount) + 1;
                
                Console.WriteLine($"[BeliefEngine] 🧬 [믿음 와전 병합] 유사도 {maxSimilarity:F3}");
                Console.WriteLine($"  => {listener.Persona.Name}의 기존 믿음 와전.");
                Console.WriteLine($"     * 기존: \"{oldContent}\"");
                Console.WriteLine($"     * 신규: \"{sharedContent}\"");
            }
        }
        else
        {
            var newBelief = new Belief
            {
                BeliefId = originalBelief.BeliefId,
                SubjectId = originalBelief.SubjectId,
                Content = sharedContent,
                ContentEmbedding = sharedEmbedding,
                Type = BeliefType.Heard,
                Confidence = 0.5,
                Salience = 1.0,
                EmotionalCharge = originalBelief.EmotionalCharge,
                SourceAgentId = speaker.AgentId,
                PropagationPath = new List<string>(speakerPath) { speaker.AgentId },
                MutationCount = originalBelief.MutationCount,
                AcquiredAt = DateTime.UtcNow
            };

            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                newBelief.Confidence = Math.Clamp(rel.Trust / 100.0, 0.1, 1.0);
            }

            listener.MemoryBox.AddOrUpdateBelief(newBelief);
            Console.WriteLine($"[BeliefEngine] 🆕 [신규 믿음 습득] {listener.Persona.Name}이(가) {speaker.Persona.Name}에게서 새로운 사실을 들었습니다.");
            Console.WriteLine($"  => 내용: \"{sharedContent}\" (유사도 최댓값: {maxSimilarity:F3})");
        }
    }

    public void DecayBeliefs(AgentInstance agent, int currentTick)
    {
        var toEvict = new List<string>();

        foreach (var kvp in agent.MemoryBox.Beliefs)
        {
            var beliefId = kvp.Key;
            var belief = kvp.Value;

            string trackingKey = $"{agent.AgentId}_{beliefId}";
            if (!_lastDecayedTicks.TryGetValue(trackingKey, out int lastTick))
            {
                _lastDecayedTicks[trackingKey] = currentTick;
                continue;
            }

            int ticksPassed = currentTick - lastTick;
            if (ticksPassed <= 0) continue;

            _lastDecayedTicks[trackingKey] = currentTick;

            double salienceDecayRate = belief.Type switch
            {
                BeliefType.Core => 0.001,
                BeliefType.Witnessed => 0.002,
                BeliefType.Heard => 0.005,
                BeliefType.Overheard => 0.010,
                _ => 0.005
            };

            if (belief.SharedWith.Count > 0)
            {
                salienceDecayRate *= 0.5;
            }

            belief.Salience = Math.Clamp(belief.Salience - (salienceDecayRate * ticksPassed), 0.0, 1.0);

            double emotionalDecayRate = 0.0005;
            belief.EmotionalCharge = Math.Clamp(belief.EmotionalCharge - (emotionalDecayRate * ticksPassed), 0.0, 1.0);

            if (belief.Salience < 0.1 && belief.Type != BeliefType.Core)
            {
                toEvict.Add(beliefId);
            }
        }

        foreach (var id in toEvict)
        {
            if (agent.MemoryBox.Beliefs.TryRemove(id, out var removed))
            {
                _lastDecayedTicks.Remove($"{agent.AgentId}_{id}");
                Console.WriteLine($"[BeliefEngine] 🗑️ [기억 망각/도태] {agent.Persona.Name}이(가) '{removed.Content}' 정보를 오랜 방치로 잊었습니다 (Salience: {removed.Salience:F3})");
            }
        }
    }

    public void PropagateCausalCascade(AgentInstance agent, string parentBeliefId)
    {
        if (!agent.MemoryBox.Beliefs.TryGetValue(parentBeliefId, out var parentBelief)) return;

        var children = agent.MemoryBox.Beliefs.Values
            .Where(b => b.DerivedFrom == parentBeliefId)
            .ToList();

        foreach (var child in children)
        {
            double oldConfidence = child.Confidence;
            // Causal propagation: Child's confidence scales with parent's confidence.
            child.Confidence = Math.Clamp(child.Confidence * parentBelief.Confidence, 0.0, 1.0);
            
            Console.WriteLine($"[BeliefEngine] 🔗 Causal cascade: Belief '{child.Content}' confidence updated {oldConfidence:F2} -> {child.Confidence:F2} (derived from parent '{parentBeliefId}')");
            
            // Recursively propagate
            PropagateCausalCascade(agent, child.BeliefId);
        }
    }

    // 🆕 4단계: 피격 시 트라우마 메모리 생성 및 관계 대폭 하락
    public async Task ProcessCombatEventAsync(AgentInstance attacker, AgentInstance victim, float damage, string weapon)
    {
        string eventContent = $"I was attacked by {attacker.Persona.Name} with {weapon} and took {damage} damage.";
        float[] embedding = await GetOrComputeEmbeddingHelperAsync(eventContent);

        // 1. 트라우마 기억 생성
        var traumaBelief = new Belief
        {
            BeliefId = "combat_" + Guid.NewGuid().ToString("N"),
            SubjectId = victim.AgentId,
            Content = eventContent,
            ContentEmbedding = embedding,
            Type = BeliefType.Witnessed, // 본인이 직접 경험한 사실
            Confidence = 1.0,
            Salience = 1.0,              // 선명도 최상
            EmotionalCharge = 1.0,       // 트라우마적 감정 상태
            SourceAgentId = attacker.AgentId,
            AcquiredAt = DateTime.UtcNow
        };

        victim.MemoryBox.AddOrUpdateBelief(traumaBelief);
        Console.WriteLine($"[BeliefEngine] 🩹 [트라우마 기억 형성] NPC '{victim.Persona.Name}'가 '{attacker.Persona.Name}'에게 공격당해 상처를 입었습니다.");

        // 2. 가해자에 대한 관계성 대폭 악화 (호감도 Liking -30, 신뢰도 Trust -40)
        var relationship = victim.RelationshipMap.AddOrUpdate(
            attacker.AgentId,
            _ => new Relationship { TargetAgentId = attacker.AgentId, Liking = -30, Trust = 10 },
            (_, existing) =>
            {
                existing.Liking = Math.Max(-100, existing.Liking - 30);
                existing.Trust = Math.Max(-100, existing.Trust - 40);
                return existing;
            }
        );

        // 3. 관계성 변동 정보를 TrackChange에 푸시하여 다음 틱에 C++ 서버로 동기화하도록 유도
        RelationshipChangeTracker.TrackChange(victim.NumericId, attacker.NumericId, relationship.Liking, relationship.Trust);
        Console.WriteLine($"[BeliefEngine] 👿 [적대화 완료] '{victim.Persona.Name}' -> '{attacker.Persona.Name}' 관계성 악화 (Liking: {relationship.Liking}, Trust: {relationship.Trust})");
    }
}
