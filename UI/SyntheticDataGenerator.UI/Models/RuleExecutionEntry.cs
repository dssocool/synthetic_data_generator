namespace SyntheticDataGenerator.UI.Models;

public sealed class RuleExecutionEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int RowsPerTable { get; set; }
    public int Seed { get; set; }
    public int TotalRowsAffected { get; set; }
    public int TableCount { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public string StartedAtDisplay => StartedAt.LocalDateTime.ToString("g");

    public string DurationDisplay
    {
        get
        {
            if (CompletedAt is null)
                return "—";

            var duration = CompletedAt.Value - StartedAt;
            return duration.TotalSeconds < 60
                ? $"{duration.TotalSeconds:F1}s"
                : $"{duration.TotalMinutes:F1}m";
        }
    }

    public string StatusDisplay => Success ? "Success" : "Failed";
}
