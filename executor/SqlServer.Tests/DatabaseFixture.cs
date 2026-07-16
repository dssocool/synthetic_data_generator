using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SyntheticDataGenerator.Tests;

public class DatabaseFixture : IAsyncLifetime
{
    private readonly string _masterConnectionString;

    public string DatabaseName { get; } = $"SyntheticDataGenTest_{Guid.NewGuid():N}";

    public string ConnectionString { get; }

    public DatabaseFixture()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddYamlFile("appsettings.yaml", optional: false)
            .Build();

        var baseConnStr = config["ConnectionString"]
            ?? throw new InvalidOperationException("ConnectionString is required in appsettings.yaml");

        var builder = new SqlConnectionStringBuilder(baseConnStr);
        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;

        builder.InitialCatalog = DatabaseName;
        ConnectionString = builder.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();

        await using var checkCmd = new SqlCommand(
            "SELECT DB_ID(@DbName)", connection);
        checkCmd.Parameters.AddWithValue("@DbName", DatabaseName);
        var result = await checkCmd.ExecuteScalarAsync();

        if (result is not null && result != DBNull.Value)
            throw new InvalidOperationException(
                $"Database '{DatabaseName}' already exists. Aborting to prevent data corruption.");

        await using var createCmd = new SqlCommand(
            $"CREATE DATABASE [{DatabaseName}]", connection);
        await createCmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_masterConnectionString);
            await connection.OpenAsync();

            await using var cmd = new SqlCommand($"""
                IF DB_ID('{DatabaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{DatabaseName}];
                END
                """, connection);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup; don't fail the test run on teardown errors
        }
    }

    /// <summary>
    /// Returns a 3-part table name: database.schema.table
    /// </summary>
    public string Qualify(string tableName, string schema = "dbo") =>
        $"{DatabaseName}.{schema}.{tableName}";

    public async Task<string> CreateSecondaryDatabaseAsync()
    {
        var name = $"SyntheticDataGenTest2_{Guid.NewGuid():N}";
        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using var createCmd = new SqlCommand($"CREATE DATABASE [{name}]", connection);
        await createCmd.ExecuteNonQueryAsync();
        return name;
    }

    public async Task DropDatabaseAsync(string databaseName)
    {
        try
        {
            await using var connection = new SqlConnection(_masterConnectionString);
            await connection.OpenAsync();
            await using var cmd = new SqlCommand($"""
                IF DB_ID('{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END
                """, connection);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort */ }
    }

    public string ConnectionStringFor(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(
            new SqlConnectionStringBuilder(_masterConnectionString).ConnectionString)
        {
            InitialCatalog = databaseName
        };
        return builder.ConnectionString;
    }

    public async Task ExecuteSqlAsync(string sql, string? databaseName = null)
    {
        await using var connection = new SqlConnection(
            databaseName is null ? ConnectionString : ConnectionStringFor(databaseName));
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ExecuteScalarAsync(string sql, string? databaseName = null)
    {
        await using var connection = new SqlConnection(
            databaseName is null ? ConnectionString : ConnectionStringFor(databaseName));
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(string sql, string? databaseName = null)
    {
        var results = new List<Dictionary<string, object?>>();
        await using var connection = new SqlConnection(
            databaseName is null ? ConnectionString : ConnectionStringFor(databaseName));
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }
            results.Add(row);
        }
        return results;
    }
}

[CollectionDefinition("Database")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
