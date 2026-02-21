using System.Text.Json;

namespace Archimedes.Core;

public static class ArchLogger
{
    public static string HumanSummary(Exception ex) =>
        $"[{DateTime.UtcNow:O}] FAILED: {ex.Message}";

    public static string MachineTrace(Exception ex) =>
        JsonSerializer.Serialize(new
        {
            timestamp = DateTime.UtcNow,
            type = ex.GetType().Name,
            message = ex.Message,
            stack = ex.StackTrace,
        });
}
