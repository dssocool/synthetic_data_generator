using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Services;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddYamlFile("appsettings.yaml", optional: false)
    .Build();

var baseConnectionString = config["ConnectionString"]
    ?? throw new InvalidOperationException("ConnectionString is required in appsettings.yaml");

var databaseName = config["DatabaseName"];
var connectionString = string.IsNullOrWhiteSpace(databaseName)
    ? baseConnectionString
    : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseConnectionString)
        { InitialCatalog = databaseName }.ConnectionString;

var rowsPerTable = int.TryParse(config["RowsPerTable"], out var r) ? r : 100;
var seed = int.TryParse(config["Seed"], out var s) ? s : (int?)null;
var schemaFilter = config["Schema"];
var locale = config["Locale"] ?? "en";
var tablesToInclude = config.GetSection("TablesToInclude").Get<string[]>() ?? [];
var tablesToExclude = config.GetSection("TablesToExclude").Get<string[]>() ?? [];

var orchestrator = new GeneratorOrchestrator(
    connectionString, rowsPerTable, seed, schemaFilter, locale,
    tablesToInclude, tablesToExclude);

var mode = ParseMode(args);

switch (mode)
{
    case ("generate-plan", var outputPath):
        await orchestrator.RunGeneratePlanAsync(outputPath ?? "plan.yaml");
        break;
    case ("execute-plan", var planPath):
        await orchestrator.RunExecutePlanAsync(planPath ?? throw new InvalidOperationException(
            "Usage: --execute-plan <plan-file-path>"));
        break;
    default:
        await orchestrator.RunDirectAsync();
        break;
}

return;

static (string Mode, string? Arg) ParseMode(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--generate-plan":
                return ("generate-plan", i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null);
            case "--execute-plan":
                return ("execute-plan", i + 1 < args.Length ? args[i + 1] : null);
        }
    }
    return ("direct", null);
}
