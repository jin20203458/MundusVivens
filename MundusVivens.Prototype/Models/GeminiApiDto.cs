using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MundusVivens.Prototype.Models;

public record GeminiRequest(
    [property: JsonPropertyName("systemInstruction")] Content? SystemInstruction,
    [property: JsonPropertyName("contents")] List<Content> Contents,
    [property: JsonPropertyName("safetySettings")] List<SafetySetting>? SafetySettings = null,
    [property: JsonPropertyName("generationConfig")] GenerationConfig? GenerationConfig = null
);

public record Content(
    [property: JsonPropertyName("role")] string Role, // "user" or "model"
    [property: JsonPropertyName("parts")] List<Part> Parts
);

public record Part(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("thought")] bool? Thought = null
);

public enum BlockThreshold
{
    BLOCK_NONE,
    BLOCK_ONLY_HIGH,
    BLOCK_MEDIUM_AND_ABOVE,
    BLOCK_LOW_AND_ABOVE
}

public enum ThinkingLevel
{
    minimal,
    low,
    medium,
    high
}

public record ThinkingConfig(
    [property: JsonPropertyName("thinkingLevel")] ThinkingLevel Level
);

public record SafetySetting(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("threshold")] BlockThreshold Threshold
);

public record GenerationConfig(
    [property: JsonPropertyName("temperature")] float? Temperature,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("responseMimeType")] string? ResponseMimeType = "text/plain",
    [property: JsonPropertyName("responseSchema")] object? ResponseSchema = null,
    [property: JsonPropertyName("thinkingConfig")] ThinkingConfig? ThinkingConfig = null
);

public record GeminiResponse(
    [property: JsonPropertyName("candidates")] List<Candidate> Candidates,
    [property: JsonPropertyName("promptFeedback")] PromptFeedback? PromptFeedback, 
    [property: JsonPropertyName("usageMetadata")] UsageMetadata? UsageMetadata
);

public record Candidate(
    [property: JsonPropertyName("content")] Content Content,
    [property: JsonPropertyName("finishReason")] string FinishReason
);

public record UsageMetadata(
    [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount,
    [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount,
    [property: JsonPropertyName("totalTokenCount")] int TotalTokenCount,
    [property: JsonPropertyName("thoughtsTokenCount")] int? ThoughtsTokenCount = 0
);

public record PromptFeedback(
    [property: JsonPropertyName("blockReason")] string BlockReason
);
