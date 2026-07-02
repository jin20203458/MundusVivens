using MundusVivens.Prototype.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IGossipEngine
{
    int CurrentTick { get; set; }
    KnownGossip? SelectGossipToShare(AgentInstance speaker, AgentInstance listener, IReadOnlyList<KnownGossip> topKCandidates);
    Task ProcessGossipSharingAsync(AgentInstance speaker, AgentInstance listener, GossipItem originalGossip, string sharedContent);
    void ApplyGossipDecay(AgentInstance agent, int currentWorldTick);
    void DecayAgentGossips(AgentInstance agent, int currentTick);
}

public class GossipEngine : IGossipEngine
{
    public int CurrentTick { get; set; } = 0;
    private readonly IGeminiApiService _geminiApi;
    private readonly IEmbeddingCache _embeddingCache;
    private readonly Random _random = new();

    public GossipEngine(IGeminiApiService geminiApi, IEmbeddingCache embeddingCache)
    {
        _geminiApi = geminiApi;
        _embeddingCache = embeddingCache;
    }


    /// <summary>
    /// 🆕 Top-K 후보 기반 오버로드: 미리 선별된 기억 목록(Top-K) 안에서만 전파할 소문을 선택합니다.
    /// 이를 통해 NPC가 머릿속에 없는 소문을 지시받는 인지 부조화를 방지합니다.
    /// </summary>
    public KnownGossip? SelectGossipToShare(AgentInstance speaker, AgentInstance listener, IReadOnlyList<KnownGossip> topKCandidates)
    {
        // 1. Top-K 후보 중 대상이 상대방(listener)인 소문은 제외하고 필터링
        var candidateGossips = topKCandidates
            .Where(kg => kg.Gossip.Subject != listener.AgentId)
            // SubjectiveBelief가 0.2 이상인 것만 전파 후보로 선택 (희미한 기억 배제)
            .Where(kg => kg.SubjectiveBelief >= 0.2)
            // 아직 상대방이 모르는 소문이거나, 상대방이 알아도 내가 더 강하게 믿는 소문 우선
            .Where(kg => !listener.KnownGossips.ContainsKey(kg.Gossip.GossipId) || 
                         kg.SubjectiveBelief > listener.KnownGossips[kg.Gossip.GossipId].SubjectiveBelief)
            .ToList();

        if (!candidateGossips.Any()) return null;

        // 2. 외향성 및 호감도에 따른 전파 확률 계산
        // 외향성이 높을수록 소문을 더 잘 퍼뜨림
        double shareChance = speaker.Persona.Extroversion;
        if (speaker.RelationshipMap.TryGetValue(listener.AgentId, out var rel))
        {
            shareChance += (rel.Liking + rel.Trust) / 400.0; // -0.5 ~ +0.5 보정
        }

        shareChance = Math.Clamp(shareChance, 0.1, 0.95);

        // 테스트용: 프로토타입 단계에서는 항상 소문을 발설할 확률을 높게 잡음
        // 단, 에바(Extroversion = 0.9)는 거의 무조건 발설
        if (speaker.Persona.Extroversion < 0.8 && _random.NextDouble() > shareChance)
        {
            return null;
        }

        // 3. 기억 속 후보 중 믿음이 가장 강한 소문 선택
        return candidateGossips.OrderByDescending(kg => kg.SubjectiveBelief).FirstOrDefault();
    }

    private async Task<float[]> GetOrComputeEmbeddingHelperAsync(string text)
    {
        return await _embeddingCache.GetOrComputeEmbeddingAsync(text, async t =>
        {
            return await _geminiApi.GetEmbeddingAsync(t);
        });
    }

    public async Task ProcessGossipSharingAsync(AgentInstance speaker, AgentInstance listener, GossipItem originalGossip, string sharedContent)
    {
        // 1. 화자가 해당 소문을 전파했음을 표시 (Decay 감속 혜택 적용)
        List<string> speakerPath = new();
        if (speaker.KnownGossips.TryGetValue(originalGossip.GossipId, out var speakerKnown))
        {
            speakerPath = new List<string>(speakerKnown.PropagationPath);
            speakerKnown.HasSharedWithOthers = true;
        }

        // 전달받은 소문 텍스트에 대한 임베딩 벡터 생성 및 캐싱
        float[] sharedEmbedding = await GetOrComputeEmbeddingHelperAsync(sharedContent);

        // 원본 소문 자체에 아직 임베딩이 없다면 채워줌
        if (originalGossip.ContentEmbedding == null)
        {
            originalGossip.ContentEmbedding = await GetOrComputeEmbeddingHelperAsync(originalGossip.Content);
        }

        // 리스너가 이미 동일 GossipId의 소문을 가지고 있는 경우 (1단계: ID 직접 매칭)
        if (listener.KnownGossips.TryGetValue(originalGossip.GossipId, out var directKnown))
        {
            directKnown.LastReinforcedAt = DateTime.UtcNow;

            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                double impact = (rel.Trust / 100.0) * 0.2; // 최대 0.2 상승
                directKnown.SubjectiveBelief = Math.Clamp(directKnown.SubjectiveBelief + impact, 0.0, 1.0);
            }

            // 변형 여부 판단
            bool isMutated = !originalGossip.Content.Trim().Equals(sharedContent.Trim(), StringComparison.OrdinalIgnoreCase);
            if (isMutated && originalGossip.MutationCount + 1 > directKnown.Gossip.MutationCount)
            {
                directKnown.Gossip.Content = sharedContent;
                directKnown.Gossip.ContentEmbedding = sharedEmbedding; // 와전 시 임베딩 동시 갱신 규칙!
                directKnown.Gossip.MutationCount = originalGossip.MutationCount + 1;
                Console.WriteLine($"[GossipEngine] 소문 업데이트 (ID 일치/누적 와전): {listener.Persona.Name}이(가) 더 왜곡된 내용을 습득했습니다 -> \"{sharedContent}\"");
            }
            return;
        }

        // ID 매칭 실패 시 (2단계 및 3단계: Subject 필터링 및 임베딩 코사인 유사도 검증)
        KnownGossip? matchedKnownGossip = null;
        double maxSimilarity = 0;

        var subjectCandidates = listener.KnownGossips.Values
            .Where(kg => kg.Gossip.Subject == originalGossip.Subject)
            .ToList();

        foreach (var candidate in subjectCandidates)
        {
            if (candidate.Gossip.ContentEmbedding == null)
            {
                candidate.Gossip.ContentEmbedding = await GetOrComputeEmbeddingHelperAsync(candidate.Gossip.Content);
            }

            double similarity = EmbeddingCache.CosineSimilarity(sharedEmbedding, candidate.Gossip.ContentEmbedding);
            if (similarity > maxSimilarity)
            {
                maxSimilarity = similarity;
                matchedKnownGossip = candidate;
            }
        }

        bool isMeaningSame = maxSimilarity >= 0.82;

        if (isMeaningSame && matchedKnownGossip != null)
        {
            // 임베딩 코사인 유사도 0.82 이상: 동일 소문의 변형으로 간주하고 병합
            matchedKnownGossip.LastReinforcedAt = DateTime.UtcNow;

            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                double impact = (rel.Trust / 100.0) * 0.2;
                matchedKnownGossip.SubjectiveBelief = Math.Clamp(matchedKnownGossip.SubjectiveBelief + impact, 0.0, 1.0);
            }

            bool isTextMutated = !matchedKnownGossip.Gossip.Content.Trim().Equals(sharedContent.Trim(), StringComparison.OrdinalIgnoreCase);
            if (isTextMutated)
            {
                string oldContent = matchedKnownGossip.Gossip.Content;
                matchedKnownGossip.Gossip.Content = sharedContent;
                matchedKnownGossip.Gossip.ContentEmbedding = sharedEmbedding; // 와전 시 임베딩 동시 갱신 규칙!
                matchedKnownGossip.Gossip.MutationCount = Math.Max(matchedKnownGossip.Gossip.MutationCount, originalGossip.MutationCount) + 1;
                
                Console.WriteLine($"[GossipEngine] 🧬 [소문 와전 병합] 유사도 {maxSimilarity:F3} 로 동일 소문 판단.");
                Console.WriteLine($"  => {listener.Persona.Name}의 기존 소문이 와전되었습니다.");
                Console.WriteLine($"     * 기존: \"{oldContent}\"");
                Console.WriteLine($"     * 신규: \"{sharedContent}\"");
                Console.WriteLine($"     * 누적 변형 횟수: {matchedKnownGossip.Gossip.MutationCount}");
            }
        }
        else
        {
            // 코사인 유사도가 0.82 미만: 완전히 새로운 소문으로 간주
            var newKnown = new KnownGossip
            {
                Gossip = new GossipItem
                {
                    GossipId = originalGossip.GossipId,
                    Subject = originalGossip.Subject,
                    Content = sharedContent,
                    ContentEmbedding = sharedEmbedding,
                    SourceAgentId = originalGossip.SourceAgentId,
                    BaseCredibility = originalGossip.BaseCredibility,
                    MutationCount = originalGossip.MutationCount
                },
                SubjectiveBelief = 0.5,
                DirectInformantAgentId = speaker.AgentId,
                PropagationPath = new List<string>(speakerPath) { speaker.AgentId },
                AcquiredAt = DateTime.UtcNow,
                LastReinforcedAt = DateTime.UtcNow,
                LastDecayedAtTick = CurrentTick
            };

            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                newKnown.SubjectiveBelief = Math.Clamp(rel.Trust / 100.0, 0.1, 1.0);
            }

            listener.KnownGossips[newKnown.Gossip.GossipId] = newKnown;
            Console.WriteLine($"[GossipEngine] 🆕 [신규 소문 습득] {listener.Persona.Name}이(가) {speaker.Persona.Name}에게서 신규 소문을 들었습니다.");
            Console.WriteLine($"  => 내용: \"{sharedContent}\" (유사도 최댓값: {maxSimilarity:F3})");
        }
    }

    public void ApplyGossipDecay(AgentInstance agent, int currentWorldTick)
    {
        DecayAgentGossips(agent, currentWorldTick);
    }

    public void DecayAgentGossips(AgentInstance agent, int currentTick)
    {
        var toRemove = new List<string>();

        foreach (var kvp in agent.KnownGossips)
        {
            var gossipId = kvp.Key;
            var knownGossip = kvp.Value;

            if (knownGossip.LastDecayedAtTick == 0)
            {
                knownGossip.LastDecayedAtTick = currentTick;
                continue;
            }

            int ticksPassed = currentTick - knownGossip.LastDecayedAtTick;
            if (ticksPassed <= 0) continue;

            double decayRate = 0.005;
            if (knownGossip.HasSharedWithOthers)
            {
                decayRate *= 0.5; // 전파한 소문은 느리게 쇠퇴
            }

            knownGossip.SubjectiveBelief -= decayRate * ticksPassed;
            knownGossip.LastDecayedAtTick = currentTick;

            if (knownGossip.SubjectiveBelief < 0.1)
            {
                toRemove.Add(gossipId);
            }
        }

        foreach (var gossipId in toRemove)
        {
            if (agent.KnownGossips.Remove(gossipId, out var removed))
            {
                Console.WriteLine($"[GossipEngine] 🗑️ [소문 소멸(Lazy)] {agent.Persona.Name}의 '{removed.Gossip.Content}' 소문이 망각되었습니다 (Belief: {removed.SubjectiveBelief:F3})");
            }
        }
    }
}
