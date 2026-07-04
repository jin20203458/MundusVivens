using System;
using System.Collections.Generic;

namespace MundusVivens.Prototype.Models
{
    public class PlayerDialogueSession
    {
        private static long _sessionIdSequence = 0;
        public static ulong GenerateSessionId() => (ulong)System.Threading.Interlocked.Increment(ref _sessionIdSequence);

        public ulong SessionId { get; }
        public string PlayerId { get; }
        public string NpcId { get; }
        public List<ChatMessage> ConversationHistory { get; } = new();
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
        public string? NpcBeliefIdToShare { get; set; }
        public string ConversationSummary { get; set; } = string.Empty;

        public PlayerDialogueSession(string playerId, string npcId)
        {
            SessionId = GenerateSessionId();
            PlayerId = playerId;
            NpcId = npcId;
        }
    }
}
