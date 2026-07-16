using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddYamlFile("appsettings.yaml", optional: false)
    .Build();

var connectionString = config["ConnectionString"]
    ?? throw new InvalidOperationException("ConnectionString is required in appsettings.yaml");

var scope = new ScopeConfig(
    include: ScopeConfig.ParseInclude(config.GetSection("Include")),
    exclude: ScopeConfig.ParseExclude(config.GetSection("Exclude")),
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

// The tool runs only in insert mode. The `update` subcommand was removed but
// is still rejected explicitly so existing scripts fail loudly instead of
// silently switching to insert.
if (args.Any(a => string.Equals(a, "update", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("Error: 'update' mode is no longer supported. Re-run without the 'update' argument to insert synthetic rows.");
    return 1;
}

if (args.Length > 0 && !args.All(a => string.Equals(a, "insert", StringComparison.Ordinal)))
{
    PrintUsage();
    return 1;
}

await orchestrator.RunDirectAsync("insert");
return 0;

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --                                    Insert synthetic data into the configured tables");
    Console.WriteLine();
    Console.WriteLine("Tables to populate are configured in appsettings.yaml (Include / Exclude).");
    Console.WriteLine("Each run writes the generated plan to ./plan.yaml in the current folder for inspection.");
}
