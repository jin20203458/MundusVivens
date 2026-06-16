using System;
using System.Collections.Generic;

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

public class Episode
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TargetName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public class CoreFact
{
    public string Content { get; set; } = string.Empty;
    public int Importance { get; set; } = 5; // 1 ~ 10

    public CoreFact() { }
    public CoreFact(string content, int importance)
    {
        Content = content;
        Importance = importance;
    }
}

public class MemoryBox
{
    public List<ChatMessage> ActiveConversation { get; set; } = new();
    public Queue<Episode> EpisodicMemories { get; set; } = new();
    public List<CoreFact> CoreMemories { get; set; } = new();

    public const int MaxEpisodes = 20;
    public const int MaxCoreMemories = 5;

    public void AddEpisode(Episode episode)
    {
        EpisodicMemories.Enqueue(episode);
        if (EpisodicMemories.Count > MaxEpisodes)
        {
            EpisodicMemories.Dequeue();
        }
    }
}
