using Microsoft.Data.SqlClient;

namespace SyntheticDataGenerator.Tests;

public class DatabaseFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True";

    public string DatabaseName { get; } = $"SyntheticDataGenTest_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={DatabaseName};Integrated Security=True;Encrypt=True;TrustServerCertificate=True";

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(MasterConnectionString);
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
            await using var connection = new SqlConnection(MasterConnectionString);
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

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ExecuteScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(string sql)
    {
        var results = new List<Dictionary<string, object?>>();
        await using var connection = new SqlConnection(ConnectionString);
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
