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

var parsed = ParseArgs(args);

switch (parsed.Subcommand)
{
    case "bootstrap" when parsed.GeneratePlan:
        await orchestrator.RunGeneratePlanAsync(parsed.Arg ?? "plan.yaml", "bootstrap");
        break;
    case "bootstrap":
        await orchestrator.RunDirectAsync("bootstrap");
        break;
    case "update" when parsed.GeneratePlan:
        if (parsed.ColumnsFile is null)
        {
            PrintUsage();
            return 1;
        }
        await orchestrator.RunUpdateGeneratePlanAsync(
            parsed.Arg ?? "plan.yaml", parsed.ColumnsFile);
        break;
    case "update":
        if (parsed.ColumnsFile is null)
        {
            PrintUsage();
            return 1;
        }
        await orchestrator.RunUpdateDirectAsync(parsed.ColumnsFile);
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
    string? columnsFile = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "bootstrap" or "update" when subcommand is null:
                subcommand = args[i];
                break;
            case "--generate-plan" when subcommand is "bootstrap" or "update":
                generatePlan = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    arg = args[++i];
                if (subcommand == "update" && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    columnsFile = args[++i];
                break;
            case "--execute-plan":
                subcommand = "execute-plan";
                if (i + 1 < args.Length)
                    arg = args[++i];
                break;
            default:
                if (subcommand == "update" && !generatePlan && columnsFile is null
                    && !args[i].StartsWith("--"))
                    columnsFile = args[i];
                break;
        }
    }

    return new ParsedArgs(subcommand, generatePlan, arg, columnsFile);
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- bootstrap                                       Insert synthetic data directly");
    Console.WriteLine("  dotnet run -- bootstrap --generate-plan [path]                Generate a plan file without inserting");
    Console.WriteLine("  dotnet run -- update <columns-file>                           Update existing data directly");
    Console.WriteLine("  dotnet run -- update --generate-plan [plan-path] <columns-file>  Generate an update plan file");
    Console.WriteLine("  dotnet run -- --execute-plan <path>                            Execute a previously generated plan");
    Console.WriteLine();
    Console.WriteLine("The <columns-file> is a YAML file listing columns to update per table:");
    Console.WriteLine("  dbo.Users:");
    Console.WriteLine("    - FirstName");
    Console.WriteLine("    - LastName");
}

record ParsedArgs(string? Subcommand, bool GeneratePlan, string? Arg, string? ColumnsFile = null);
