using System.Text.Json;

namespace Archimedes.Core;

public static class ArchLogger
{
    public static string HumanSummary(Exception ex) =>
        $"[{DateTime.UtcNow:O}] FAILED: {Redactor.Redact(ex.Message)}";

    public static string MachineTrace(Exception ex) =>
        JsonSerializer.Serialize(new
        {
            timestamp = DateTime.UtcNow,
            type = ex.GetType().Name,
            message = Redactor.Redact(ex.Message),
            stack = Redactor.Redact(ex.StackTrace),
        });

    public static void LogInfo(string message) =>
        Console.WriteLine($"[INFO] {Redactor.Redact(message)}");

    public static void LogWarn(string message) =>
        Console.WriteLine($"[WARN] {Redactor.Redact(message)}");

    public static void LogError(string message, Exception? ex = null)
    {
        Console.WriteLine($"[ERROR] {Redactor.Redact(message)}");
        if (ex != null)
        {
            Console.WriteLine(HumanSummary(ex));
        }
    }

    public static void LogPayload(string context, string? payload)
    {
        var meta = Redactor.SafeMetadata(payload);
        Console.WriteLine($"[PAYLOAD] {context}: len={meta["length"]} hash={meta["hash"]}");
    }
}
