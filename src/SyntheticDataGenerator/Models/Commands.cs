namespace SyntheticDataGenerator.Models;

public record ValidateScopeCommand(
    string ConnectionString,
    ScopeConfig Scope,
    string Mode);

public record ValidateScopeResult(
    bool IsValid,
    List<string> Errors,
    List<TableInfo> ScopedTables,
    IReadOnlySet<string>? SelfReferencingTables,
    Dictionary<string, HashSet<string>>? ColumnScope,
    List<ExternalDependency>? ExternalDependencies = null,
    List<CustomDependencyGroup>? CustomDependencies = null,
    Dictionary<string, ValueListBinding>? StandaloneValueLists = null)
{
    public static ValidateScopeResult Failure(List<string> errors) =>
        new(false, errors, [], null, null);
}

/// <summary>
/// A standalone CustomValueLists binding ready for the planner to attach to
/// an in-scope column. Exactly one of <see cref="File"/> or <see cref="Values"/>
/// is populated.
/// </summary>
public sealed record ValueListBinding(string? File, List<string>? Values);

public record GeneratePlanCommand(
    ValidateScopeResult ValidationResult,
    ScopeConfig Scope,
    string? OutputPath,
    string Mode = "insert");

public record GeneratePlanResult(
    GenerationPlan Plan,
    string? OutputPath);

public record ExecutePlanCommand(
    GenerationPlan Plan,
    string ConnectionString,
    string? PlanBasePath,
    int? ExternalSourceBufferSize = null,
    int MaxParallelTables = 1);

public record TableExecutionDetail(
    string TableName,
    int RowsAffected,
    bool Success,
    string? ErrorMessage);

public record ExecutePlanResult(
    int TotalRowsAffected,
    List<TableExecutionDetail> Tables);

public record RevertExecutionCommand;

public record RevertExecutionResult(bool IsSupported, string? Message);
