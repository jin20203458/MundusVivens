using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Helpers;

public class TokenLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public long TotalPromptTokens { get; private set; }
    public long TotalCompletionTokens { get; private set; }
    public long TotalThinkingTokens { get; private set; }
    public long TotalTokens => TotalPromptTokens + TotalCompletionTokens;
    public double ApproximateCostUsd => (TotalPromptTokens * 0.000000075) + (TotalCompletionTokens * 0.0000003);

    public TokenLogger(string sessionId)
    {
        // AppContext.BaseDirectory 대신 프로젝트 루트 또는 현재 실행 폴더 기준
        var logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "Sessions", sessionId);
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }
        _logFilePath = Path.Combine(logDir, "TokenLog.csv");

        if (!File.Exists(_logFilePath))
        {
            File.WriteAllText(_logFilePath, "Timestamp,Model,PromptTokens,CompletionTokens,ThinkingTokens,TotalTokens,ApproxCostUsd\n", Encoding.UTF8);
        }
    }

    public async Task LogUsageAsync(string model, int promptTokens, int completionTokens, int thinkingTokens)
    {
        lock (_lock)
        {
            TotalPromptTokens += promptTokens;
            TotalCompletionTokens += completionTokens;
            TotalThinkingTokens += thinkingTokens;
        }

        var cost = (promptTokens * 0.000000075) + (completionTokens * 0.0000003);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logLine = $"{timestamp},{model},{promptTokens},{completionTokens},{thinkingTokens},{promptTokens + completionTokens},{cost:F8}\n";

        await File.AppendAllTextAsync(_logFilePath, logLine, Encoding.UTF8);
    }
}
