using System.Text.Json;

namespace Archimedes.Core;

public class SavedState
{
    public string JobId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "paused";
    public DateTime SavedAt { get; set; }
}
