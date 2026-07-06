using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Helpers;

public class LlmResponseLogger
{
    private readonly string _logDir;

    public LlmResponseLogger(string sessionId)
    {
        _logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "Sessions", sessionId, "LlmResponses");
        if (Directory.Exists(_logDir))
        {
            try
            {
                foreach (var file in Directory.GetFiles(_logDir))
                {
                    File.Delete(file);
                }
                Console.WriteLine("🧹 [LlmResponseLogger] 이전 실행의 LLM 응답 로그 파일들을 자동 초기화(Clear)했습니다.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LlmResponseLogger Warning] 이전 로그 삭제 실패: {ex.Message}");
            }
        }
        else
        {
            Directory.CreateDirectory(_logDir);
        }
    }

    public async Task LogResponseAsync(string agentId, string apiName, string responseJson)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{timestamp}_{agentId}_{apiName}.json";
            var filePath = Path.Combine(_logDir, fileName);
            await File.WriteAllTextAsync(filePath, responseJson, Encoding.UTF8);
            Console.WriteLine($"💾 [LlmResponseLogger] saved response: {fileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LlmResponseLogger Error] Failed to log response: {ex.Message}");
        }
    }
}
