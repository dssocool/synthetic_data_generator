using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public interface IDataGenerationPlanner
{
    Task<ValidateScopeResult> ValidateScopeAsync(
        ValidateScopeCommand command,
        CancellationToken ct);

    Task<GeneratePlanResult> GeneratePlanAsync(
        GeneratePlanCommand command,
        CancellationToken ct);
}
