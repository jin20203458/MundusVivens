using MundusVivens.Prototype.Models;
using MundusVivens.Prototype.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IGeminiApiService
{
    Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default);
    Task<float[]> GetEmbeddingAsync(string text); // 🆕 임베딩 생성 API 연동 (Phase 5-2 신규)
    int TotalPromptTokens { get; }
    int TotalCompletionTokens { get; }
    int TotalTokens { get; }
    double ApproximateCostUsd { get; }
    void ResetTokenStats();
}

public class GeminiApiService : IGeminiApiService
{
    private readonly HttpClient _httpClient;
    private readonly IGoogleAuthService _googleAuthService;
    private readonly AppSettings _settings;
    private readonly TokenLogger _tokenLogger;
    
    public int TotalPromptTokens => (int)_tokenLogger.TotalPromptTokens;
    public int TotalCompletionTokens => (int)_tokenLogger.TotalCompletionTokens;
    public int TotalTokens => (int)_tokenLogger.TotalTokens;
    public double ApproximateCostUsd => _tokenLogger.ApproximateCostUsd;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public GeminiApiService(HttpClient httpClient, IGoogleAuthService googleAuthService, TokenLogger tokenLogger)
    {
        _httpClient = httpClient;
        _googleAuthService = googleAuthService;
        _tokenLogger = tokenLogger;
        _settings = LoadSettings();
    }

    private AppSettings LoadSettings()
    {
        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppSettings.json");
        if (!File.Exists(filePath))
        {
            Console.WriteLine("[Warn] AppSettings.json이 없습니다. 기본 설정을 사용합니다.");
            return new AppSettings();
        }

        try
        {
            string jsonString = File.ReadAllText(filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(jsonString, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] AppSettings.json 로드 실패: {ex.Message}");
            return new AppSettings();
        }
    }

    private string GetModelName(ModelTier tier) => tier switch
    {
        ModelTier.Pro => "models/gemini-3.1-pro-preview",
        ModelTier.Flash35 => "models/gemini-3.5-flash",
        ModelTier.FlashLite => "models/gemini-3.1-flash-lite-preview",
        ModelTier.Flash3 => "models/gemini-3-flash-preview",
        _ => "models/gemini-3.5-flash"
    };

    private string GetVertexModelName(ModelTier tier) => tier switch
    {
        ModelTier.Pro => "gemini-3.1-pro-preview",
        ModelTier.Flash35 => "gemini-3.5-flash",
        ModelTier.FlashLite => "gemini-3.1-flash-lite-preview",
        ModelTier.Flash3 => "gemini-3-flash-preview",
        _ => "gemini-3.5-flash"
    };

    public void ResetTokenStats()
    {
    }

    public async Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
    {
        string requestUri;
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "");
        string jsonPayload = JsonSerializer.Serialize(request, _jsonOptions);
        httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        ModelTier selectedModelTier = overrideTier ?? (_settings.SelectedModel.ToLower() switch
        {
            "pro" => ModelTier.Pro,
            "flash3" => ModelTier.Flash3,
            "flashlite" => ModelTier.FlashLite,
            _ => ModelTier.Flash35
        });

        if (_settings.UseVertexAI)
        {
            if (string.IsNullOrWhiteSpace(_settings.ProjectId))
                return "[System Error]: 구글 클라우드 Project ID가 설정되지 않았습니다.";

            string location = string.IsNullOrWhiteSpace(_settings.Location) ? "global" : _settings.Location.ToLower();
            string hostName = location == "global" ? "aiplatform.googleapis.com" : $"{location}-aiplatform.googleapis.com";
            string modelName = GetVertexModelName(selectedModelTier);
            requestUri = $"https://{hostName}/v1beta1/projects/{_settings.ProjectId}/locations/{location}/publishers/google/models/{modelName}:generateContent";

            try
            {
                string token = await _googleAuthService.GetGoogleAccessTokenAsync(cancellationToken);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch (Exception ex)
            {
                return $"[System Error]: 인증 토큰 획득 실패. {ex.Message}";
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                return "[System Error]: API 키가 설정되지 않았습니다.";

            string modelName = GetModelName(selectedModelTier);
            requestUri = $"https://generativelanguage.googleapis.com/v1beta/{modelName}:generateContent?key={_settings.ApiKey}";
        }

        httpRequest.RequestUri = new Uri(requestUri);

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"API 통신 실패 ({(int)response.StatusCode}): {errorContent}");
            }

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            // Console.WriteLine($"[DEBUG] Response: {responseBody}");
            var responseData = JsonSerializer.Deserialize<GeminiResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (responseData?.UsageMetadata != null)
            {
                string modelName = _settings.UseVertexAI ? GetVertexModelName(selectedModelTier) : GetModelName(selectedModelTier);
                await _tokenLogger.LogUsageAsync(
                    modelName, 
                    responseData.UsageMetadata.PromptTokenCount, 
                    responseData.UsageMetadata.CandidatesTokenCount, 
                    0
                );
            }

            if (responseData?.PromptFeedback != null && !string.IsNullOrEmpty(responseData.PromptFeedback.BlockReason))
            {
                return $"[System Error]: 프롬프트 차단됨. 사유: {responseData.PromptFeedback.BlockReason}";
            }

            if (responseData?.Candidates != null && responseData.Candidates.Count > 0)
            {
                var candidate = responseData.Candidates[0];
                if (candidate.FinishReason != "STOP" && candidate.FinishReason != "MAX_TOKENS")
                {
                    return $"[System Error]: 생성 비정상 중단. 사유: {candidate.FinishReason}";
                }

                if (candidate.Content?.Parts != null)
                {
                    var finalAnswer = new StringBuilder();
                    foreach (var part in candidate.Content.Parts)
                    {
                        if (string.IsNullOrEmpty(part.Text)) continue;
                        if (part.Thought == true) continue;
                        if (part.Text.Trim().Equals("thought", StringComparison.OrdinalIgnoreCase)) continue;

                        finalAnswer.Append(part.Text);
                    }

                    return finalAnswer.ToString();
                }
            }

            Console.WriteLine($"[Error] Gemini API 응답 데이터 오류. Raw Response: {responseBody}");
            return "[System Error]: 응답 데이터가 유효하지 않습니다.";
        }
        catch (Exception ex)
        {
            return $"[System Error]: 호출 실패 - {ex.Message}";
        }
    }

    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        string requestUri;
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "");

        if (_settings.UseVertexAI)
        {
            if (string.IsNullOrWhiteSpace(_settings.ProjectId))
                throw new Exception("구글 클라우드 Project ID가 설정되지 않았습니다.");

            string location = string.IsNullOrWhiteSpace(_settings.Location) ? "global" : _settings.Location.ToLower();
            string embedLocation = location == "global" ? "us-central1" : location;
            string hostName = $"{embedLocation}-aiplatform.googleapis.com";
            
            // Vertex AI는 predict 엔드포인트를 사용해야 합니다.
            requestUri = $"https://{hostName}/v1beta1/projects/{_settings.ProjectId}/locations/{embedLocation}/publishers/google/models/text-embedding-004:predict";

            var requestPayload = new
            {
                instances = new[]
                {
                    new { content = text }
                }
            };
            string jsonPayload = JsonSerializer.Serialize(requestPayload);
            httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                string token = await _googleAuthService.GetGoogleAccessTokenAsync();
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch (Exception ex)
            {
                throw new Exception($"[System Error]: 인증 토큰 획득 실패. {ex.Message}");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new Exception("API 키가 설정되지 않았습니다.");

            requestUri = $"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent?key={_settings.ApiKey}";

            var requestPayload = new
            {
                content = new
                {
                    parts = new[]
                    {
                        new { text = text }
                    }
                }
            };
            string jsonPayload = JsonSerializer.Serialize(requestPayload);
            httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        }

        httpRequest.RequestUri = new Uri(requestUri);

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest);
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Gemini Embedding API 통신 실패 ({(int)response.StatusCode}): {errorContent}");
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            
            JsonElement valuesObj;
            if (_settings.UseVertexAI)
            {
                // Vertex AI는 predictions[0].embeddings.values 포맷을 가집니다.
                var predictionsObj = doc.RootElement.GetProperty("predictions");
                var firstPrediction = predictionsObj[0];
                var embeddingsObj = firstPrediction.GetProperty("embeddings");
                valuesObj = embeddingsObj.GetProperty("values");
            }
            else
            {
                // Google AI Studio는 embedding.values 포맷을 가집니다.
                var embeddingObj = doc.RootElement.GetProperty("embedding");
                valuesObj = embeddingObj.GetProperty("values");
            }

            var result = new float[valuesObj.GetArrayLength()];
            int idx = 0;
            foreach (var val in valuesObj.EnumerateArray())
            {
                result[idx++] = val.GetSingle();
            }

            return result;
        }
        catch (Exception ex)
        {
            throw new Exception($"Gemini Embedding API 호출 실패: {ex.Message}");
        }
    }
}
