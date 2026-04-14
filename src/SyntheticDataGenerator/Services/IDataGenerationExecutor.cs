using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public interface IDataGenerationExecutor
{
    Task<ExecutePlanResult> ExecutePlanAsync(
        ExecutePlanCommand command,
        CancellationToken ct);

    Task<RevertExecutionResult> RevertExecutionAsync(
        RevertExecutionCommand command,
        CancellationToken ct);
}
