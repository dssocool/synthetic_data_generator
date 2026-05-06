namespace SyntheticDataGenerator.Services;

/// <summary>
/// Picks values uniformly from a CustomValueLists-backed source. Two
/// constructors:
///   * <see cref="ValueListSource(string, Random?)"/> — loads a flat values
///     file (one value per line, blank lines skipped) lazily on first
///     <see cref="Pick"/> call.
///   * <see cref="ValueListSource(IEnumerable{string}, Random?)"/> — wraps an
///     in-memory list provided inline via the YAML config; no file I/O.
///
/// Either way, every value lives in memory for the run. Files are expected to
/// be small lookup lists; for large datasets prefer the live-DB
/// <see cref="ExternalSourceStreamer"/>.
/// </summary>
public sealed class ValueListSource
{
    private readonly string? _filePath;
    private readonly Random _random;
    private string[]? _values;

    // Pick / EnsureLoaded mutate _values (lazy load) and _random. Held for the
    // full Pick call so concurrent callers from parallel table tasks don't
    // race the lazy file load or the shared Random instance.
    private readonly object _pickLock = new();

    public ValueListSource(string filePath, Random? random = null)
    {
        _filePath = filePath;
        _random = random ?? new Random();
    }

    public ValueListSource(IEnumerable<string> values, Random? random = null)
    {
        _filePath = null;
        _random = random ?? new Random();
        _values = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
        if (_values.Length == 0)
            throw new InvalidOperationException(
                "CustomValueLists inline values list is empty.");
    }

    public string? FilePath => _filePath;

    public object Pick()
    {
        lock (_pickLock)
        {
            EnsureLoaded();
            return _values![_random.Next(_values.Length)];
        }
    }

    private void EnsureLoaded()
    {
        if (_values is not null) return;

        if (!File.Exists(_filePath))
            throw new InvalidOperationException(
                $"CustomValueLists file '{_filePath}' does not exist.");

        var values = File.ReadAllLines(_filePath!)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (values.Length == 0)
            throw new InvalidOperationException(
                $"CustomValueLists file '{_filePath}' is empty.");

        _values = values;
    }
}
