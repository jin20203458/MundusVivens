using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Helpers;

public class MemoryEventLogger
{
    private readonly string _logFilePath;

    public MemoryEventLogger(string sessionId)
    {
        var logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "Sessions", sessionId);
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }
        _logFilePath = Path.Combine(logDir, "MemoryLog.txt");
    }

    public async Task LogMemoryEventAsync(string eventDescription)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logLine = $"[{timestamp}] {eventDescription}\n";
        await File.AppendAllTextAsync(_logFilePath, logLine, Encoding.UTF8);
    }

    public async Task<string> ReadAllLogsAsync()
    {
        if (!File.Exists(_logFilePath))
        {
            return string.Empty;
        }
        return await File.ReadAllTextAsync(_logFilePath, Encoding.UTF8);
    }
}
