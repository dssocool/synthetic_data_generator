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
    customDependencyBufferSize: int.TryParse(config["CustomDependencyBufferSize"], out var b) ? b : 10_000);

var planner = new DataGenerationPlanner();
var executor = new DataGenerationExecutor();
var orchestrator = new GeneratorOrchestrator(connectionString, scope, planner, executor);

var parsed = ParseArgs(args);

switch (parsed.Subcommand)
{
    case "insert" when parsed.GeneratePlan:
        await orchestrator.RunGeneratePlanAsync(parsed.Arg ?? "plan.yaml", "insert");
        break;
    case "insert":
        await orchestrator.RunDirectAsync("insert");
        break;
    case "update" when parsed.GeneratePlan:
        await orchestrator.RunGeneratePlanAsync(parsed.Arg ?? "plan.yaml", "update");
        break;
    case "update":
        await orchestrator.RunDirectAsync("update");
        break;
    case "execute-plan":
        await orchestrator.RunExecutePlanAsync(parsed.Arg ?? throw new InvalidOperationException(
            "Usage: dotnet run -- --execute-plan <plan-file-path>"));
        break;
    default:
        PrintUsage();
        return 1;
}

return 0;

static ParsedArgs ParseArgs(string[] args)
{
    string? subcommand = null;
    var generatePlan = false;
    string? arg = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "insert" or "update" when subcommand is null:
                subcommand = args[i];
                break;
            case "--generate-plan" when subcommand is "insert" or "update":
                generatePlan = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    arg = args[++i];
                break;
            case "--execute-plan":
                subcommand = "execute-plan";
                if (i + 1 < args.Length)
                    arg = args[++i];
                break;
        }
    }

    return new ParsedArgs(subcommand, generatePlan, arg);
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- insert                             Insert synthetic data directly");
    Console.WriteLine("  dotnet run -- insert --generate-plan [path]      Generate a plan file without inserting");
    Console.WriteLine("  dotnet run -- update                             Update existing data directly");
    Console.WriteLine("  dotnet run -- update --generate-plan [path]      Generate an update plan file");
    Console.WriteLine("  dotnet run -- --execute-plan <path>              Execute a previously generated plan");
    Console.WriteLine();
    Console.WriteLine("Tables and columns to include/update are configured in appsettings.yaml (TablesToInclude).");
}

record ParsedArgs(string? Subcommand, bool GeneratePlan, string? Arg);
