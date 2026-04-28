namespace SyntheticDataGenerator.Services;

/// <summary>
/// Picks values uniformly from a flat values file (one value per line, blank
/// lines skipped). Used by CustomDependencies groups whose external root is
/// backed by a CustomValueLists entry — values come from the file instead of
/// being streamed from a live database cursor.
///
/// Lifecycle: lazy-loads the file on first <see cref="Pick"/> call and keeps
/// every line in memory for the run. Files are expected to be small lookup
/// lists; for large datasets prefer the live-DB <see cref="ExternalSourceStreamer"/>.
/// </summary>
public sealed class ValueListSource
{
    private readonly string _filePath;
    private readonly Random _random;
    private string[]? _values;

    public ValueListSource(string filePath, Random? random = null)
    {
        _filePath = filePath;
        _random = random ?? new Random();
    }

    public string FilePath => _filePath;

    public object Pick()
    {
        EnsureLoaded();
        return _values![_random.Next(_values.Length)];
    }

    private void EnsureLoaded()
    {
        if (_values is not null) return;

        if (!File.Exists(_filePath))
            throw new InvalidOperationException(
                $"CustomValueLists file '{_filePath}' does not exist.");

        var values = File.ReadAllLines(_filePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (values.Length == 0)
            throw new InvalidOperationException(
                $"CustomValueLists file '{_filePath}' is empty.");

        _values = values;
    }
}
