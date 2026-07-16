using System.Data;

namespace SyntheticDataGenerator.UI.Models;

public sealed class TablePreviewResult
{
    public required string TableName { get; init; }
    public required DataTable DataTable { get; init; }
}

public sealed class SyntheticDataPreviewResult
{
    public bool Success => string.IsNullOrWhiteSpace(ErrorMessage);
    public string? ErrorMessage { get; init; }
    public string? AppsettingsPath { get; init; }
    public IReadOnlyList<TablePreviewResult> Tables { get; init; } = [];

    public static SyntheticDataPreviewResult Failed(string message) =>
        new() { ErrorMessage = message };
}
