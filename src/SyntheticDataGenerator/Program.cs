using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Models;
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

var scope = new ScopeConfig(
    schemaFilter: ScopeConfig.ParseSchemaFilter(config.GetSection("Schema")),
    tablesToInclude: ScopeConfig.ParseTablesToInclude(config.GetSection("TablesToInclude")),
    rowsPerTable: int.TryParse(config["RowsPerTable"], out var r) ? r : 100,
    seed: int.TryParse(config["Seed"], out var s) ? s : null,
    locale: config["Locale"] ?? "en",
    customDependencies: config.GetSection("CustomDependencies").Get<string[]>(),
    customDependencyBufferSize: int.TryParse(config["CustomDependencyBufferSize"], out var b) ? b : 10_000,
    customValueLists: ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists")),
    maxParallelTables: int.TryParse(config["MaxParallelTables"], out var p) ? p : null);

var planner = new DataGenerationPlanner();
var executor = new DataGenerationExecutor();
var orchestrator = new GeneratorOrchestrator(connectionString, scope, planner, executor);

var subcommand = ParseSubcommand(args);

switch (subcommand)
{
    case "insert":
        await orchestrator.RunDirectAsync("insert");
        break;
    case "update":
        await orchestrator.RunDirectAsync("update");
        break;
    default:
        PrintUsage();
        return 1;
}

return 0;

static string? ParseSubcommand(string[] args)
{
    string? subcommand = null;
    var hasUnknown = false;

    foreach (var token in args)
    {
        switch (token)
        {
            case "insert" or "update" when subcommand is null:
                subcommand = token;
                break;
            default:
                hasUnknown = true;
                break;
        }
    }

    // Default to "insert" mode when no subcommand is supplied and no unknown
    // tokens were seen. Unknown tokens fall through to PrintUsage so typos
    // don't silently kick off a real insert run.
    if (subcommand is null && !hasUnknown)
        subcommand = "insert";

    return subcommand;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --                                    Insert synthetic data directly (default mode)");
    Console.WriteLine("  dotnet run -- insert                             Insert synthetic data directly");
    Console.WriteLine("  dotnet run -- update                             Update existing data directly");
    Console.WriteLine();
    Console.WriteLine("Tables and columns to include/update are configured in appsettings.yaml (TablesToInclude).");
    Console.WriteLine("Each run writes the generated plan to ./plan.yaml in the current folder for inspection.");
}
