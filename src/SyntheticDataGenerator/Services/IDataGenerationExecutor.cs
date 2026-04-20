using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public interface IDataGenerationExecutor
{
    Task<ExecutePlanResult> ExecutePlanAsync(
        ExecutePlanCommand command,
        CancellationToken ct,
        Action<TableExecutionDetail>? onTableComplete = null);

    Task<RevertExecutionResult> RevertExecutionAsync(
        RevertExecutionCommand command,
        CancellationToken ct);
}
