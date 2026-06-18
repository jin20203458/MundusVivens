using MundusVivens.Prototype.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MundusVivens.Prototype.Services;

public interface IGossipEngine
{
    KnownGossip? SelectGossipToShare(AgentInstance speaker, AgentInstance listener);
    void ProcessGossipSharing(AgentInstance speaker, AgentInstance listener, GossipItem originalGossip, string sharedContent);
}

public class GossipEngine : IGossipEngine
{
    private readonly Random _random = new();

    public KnownGossip? SelectGossipToShare(AgentInstance speaker, AgentInstance listener)
    {
        // 1. 내가 알고 있는 소문 목록 중 대상이 상대방(listener)인 소문은 제외하고 필터링
        var candidateGossips = speaker.KnownGossips.Values
            .Where(kg => kg.Gossip.Subject != listener.AgentId)
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

        // 3. 믿음이 가장 강한 소문 중 무작위 선택
        return candidateGossips.OrderByDescending(kg => kg.SubjectiveBelief).FirstOrDefault();
    }

    public void ProcessGossipSharing(AgentInstance speaker, AgentInstance listener, GossipItem originalGossip, string sharedContent)
    {
        // 소문 전파 및 변형 처리
        bool isMutated = !originalGossip.Content.Trim().Equals(sharedContent.Trim(), StringComparison.OrdinalIgnoreCase);

        // 화자의 기존 소문 전파 경로 조회
        List<string> speakerPath = new();
        if (speaker.KnownGossips.TryGetValue(originalGossip.GossipId, out var speakerKnown))
        {
            speakerPath = new List<string>(speakerKnown.PropagationPath);
        }

        // 상대방에게 소문 주입
        if (!listener.KnownGossips.TryGetValue(originalGossip.GossipId, out var known))
        {
            known = new KnownGossip
            {
                Gossip = new GossipItem
                {
                    GossipId = originalGossip.GossipId,
                    Subject = originalGossip.Subject,
                    Content = sharedContent,
                    SourceAgentId = originalGossip.SourceAgentId,
                    BaseCredibility = originalGossip.BaseCredibility,
                    MutationCount = originalGossip.MutationCount + (isMutated ? 1 : 0)
                },
                // 상대방(listener)이 화자(speaker)를 믿는 신뢰도만큼 이 소문을 주입받을 때의 주관적 믿음 설정
                SubjectiveBelief = 0.5,
                DirectInformantAgentId = speaker.AgentId,
                PropagationPath = new List<string>(speakerPath) { speaker.AgentId }
            };

            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                known.SubjectiveBelief = Math.Clamp(rel.Trust / 100.0, 0.1, 1.0);
            }

            listener.KnownGossips[originalGossip.GossipId] = known;
            Console.WriteLine($"[GossipEngine] 소문 전파 성공: {listener.Persona.Name}이(가) {speaker.Persona.Name}에게서 '{originalGossip.Subject}'에 대한 소문을 들었습니다.");
            if (isMutated)
            {
                Console.WriteLine($"  => [변형 감지!] \n     * 원본: \"{originalGossip.Content}\"\n     * 변형: \"{sharedContent}\"\n     * 누적 변형 횟수: {known.Gossip.MutationCount}");
            }
        }
        else
        {
            // 이미 알고 있는 소문이면 신뢰도 보정
            if (listener.RelationshipMap.TryGetValue(speaker.AgentId, out var rel))
            {
                double impact = (rel.Trust / 100.0) * 0.2; // 최대 0.2만큼 주관적 확신 상승
                known.SubjectiveBelief = Math.Clamp(known.SubjectiveBelief + impact, 0.0, 1.0);
            }
            
            // 만약 새로운 내용이 더 많이 전파/변형되었을 경우 내용 업데이트
            if (isMutated && originalGossip.MutationCount + 1 > known.Gossip.MutationCount)
            {
                known.Gossip.Content = sharedContent;
                known.Gossip.MutationCount = originalGossip.MutationCount + 1;
                Console.WriteLine($"[GossipEngine] 소문 업데이트 (누적 변형): {listener.Persona.Name}이(가) 더 왜곡된 내용을 습득했습니다 -> \"{sharedContent}\"");
            }
        }
    }
}
