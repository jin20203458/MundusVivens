using System;
using System.Collections.Generic;

namespace MundusVivens.Prototype.Models
{
    public class PlayerDialogueSession
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public string PlayerId { get; }
        public string NpcId { get; }
        public List<ChatMessage> ConversationHistory { get; } = new();
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

        public PlayerDialogueSession(string playerId, string npcId)
        {
            PlayerId = playerId;
            NpcId = npcId;
        }
    }
}
