using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

namespace SyntheticDataGenerator.UI.Services;

public sealed class SqlServerMetadataService
{
    public async Task<IReadOnlyList<string>> GetDatabasesAsync(
        string connectionString,
        CancellationToken ct = default)
    {
        var reader = new SchemaReader(connectionString);
        return await reader.GetUserDatabasesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetSchemasAsync(
        string connectionString,
        string database,
        CancellationToken ct = default)
    {
        var reader = new SchemaReader(connectionString);
        return await reader.GetSchemasAsync(database, ct);
    }

    public async Task<IReadOnlyList<string>> GetTablesAsync(
        string connectionString,
        string database,
        string schema,
        CancellationToken ct = default)
    {
        var reader = new SchemaReader(connectionString);
        return await reader.GetTablesAsync(database, schema, ct);
    }

    public async Task<TableInfo?> GetTableInfoAsync(
        string connectionString,
        string database,
        string schema,
        string table,
        CancellationToken ct = default)
    {
        var reader = new SchemaReader(connectionString);
        return await reader.GetTableInfoAsync(database, schema, table, ct);
    }

    public async Task<IReadOnlyList<string>> SearchColumnsAsync(
        string connectionString,
        string filter,
        int maxResults = 200,
        CancellationToken ct = default)
    {
        var reader = new SchemaReader(connectionString);
        return await reader.SearchColumnsAsync(filter, maxResults, ct);
    }
}
