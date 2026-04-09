using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SyntheticDataGenerator.Models;

public class UpdateColumnsSpec
{
    public Dictionary<string, List<string>> Tables { get; set; } = new();

    public static async Task<UpdateColumnsSpec> ReadAsync(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Update columns file not found: {path}", path);

        var yaml = await File.ReadAllTextAsync(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, List<string>>>(yaml)
                  ?? throw new InvalidOperationException(
                      $"Failed to deserialize update columns file: {path}");

        return new UpdateColumnsSpec { Tables = raw };
    }
}
