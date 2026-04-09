using SyntheticDataGenerator.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SyntheticDataGenerator.Services;

public class PlanGenerator
{
    private static readonly HashSet<string> StringCompatibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "varchar", "nvarchar", "char", "nchar", "text", "ntext", "xml"
    };

    private static readonly HashSet<string> UnsupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "geography", "geometry", "hierarchyid"
    };

    private static readonly Dictionary<string, (string Generator, Dictionary<string, object?>? Args)> SqlTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"]              = ("Random.Int", new() { ["min"] = 1, ["max"] = 1073741823 }),
        ["bigint"]           = ("Random.Long", new() { ["min"] = 1L, ["max"] = 4611686018427387903L }),
        ["smallint"]         = ("Random.Short", new() { ["min"] = (short)1, ["max"] = short.MaxValue }),
        ["tinyint"]          = ("Random.Byte", null),
        ["bit"]              = ("Random.Bool", null),
        ["decimal"]          = ("Random.Decimal", new() { ["min"] = 0m, ["max"] = 99999m }),
        ["numeric"]          = ("Random.Decimal", new() { ["min"] = 0m, ["max"] = 99999m }),
        ["money"]            = ("Finance.Amount", new() { ["min"] = 1m, ["max"] = 10000m }),
        ["smallmoney"]       = ("Finance.Amount", new() { ["min"] = 1m, ["max"] = 10000m }),
        ["float"]            = ("Random.Double", new() { ["min"] = 0.0, ["max"] = 99999.0 }),
        ["real"]             = ("Random.Float", new() { ["min"] = 0.0, ["max"] = 99999.0 }),
        ["datetime"]         = ("Date.Past", new() { ["yearsToGoBack"] = 5 }),
        ["datetime2"]        = ("Date.Past", new() { ["yearsToGoBack"] = 5 }),
        ["smalldatetime"]    = ("Date.Past", new() { ["yearsToGoBack"] = 5 }),
        ["date"]             = ("Date.PastDateOnly", new() { ["yearsToGoBack"] = 5 }),
        ["time"]             = ("Date.Timespan", null),
        ["datetimeoffset"]   = ("Date.PastOffset", new() { ["yearsToGoBack"] = 5 }),
        ["char"]             = ("Random.AlphaNumeric", null),
        ["nchar"]            = ("Random.AlphaNumeric", null),
        ["varchar"]          = ("Lorem.Word", null),
        ["nvarchar"]         = ("Lorem.Word", null),
        ["text"]             = ("Lorem.Sentence", null),
        ["ntext"]            = ("Lorem.Sentence", null),
        ["uniqueidentifier"] = ("Guid", null),
        ["varbinary"]        = ("Random.Bytes", null),
        ["binary"]           = ("Random.Bytes", null),
        ["image"]            = ("Random.Bytes", null),
        ["xml"]              = ("Lorem.Word", new() { ["wrapXml"] = true }),
        ["sql_variant"]      = ("Random.SqlVariant", null),
    };

    public GenerationPlan Generate(
        List<TableInfo> sortedTables,
        IReadOnlySet<string> selfReferencingTables,
        int defaultRowCount,
        int? seed,
        string locale = "en",
        string mode = "bootstrap")
    {
        var plan = new GenerationPlan
        {
            Mode = mode,
            Seed = seed,
            Locale = locale,
            Tables = []
        };

        for (var i = 0; i < sortedTables.Count; i++)
        {
            var table = sortedTables[i];
            var fkColumnNames = new HashSet<string>(
                table.ForeignKeys.Select(fk => fk.ParentColumn),
                StringComparer.OrdinalIgnoreCase);

            var tablePlan = new TablePlan
            {
                Table = table.FullName,
                Order = i + 1,
                RowCount = defaultRowCount,
                Columns = []
            };

            foreach (var col in table.Columns)
            {
                var colPlan = new ColumnPlan
                {
                    Name = col.Name,
                    SqlType = col.SqlType,
                    MaxLength = col.MaxLength,
                    Precision = col.Precision,
                    Scale = col.Scale,
                    IsNullable = col.IsNullable,
                    IsIdentity = col.IsIdentity,
                    IsPrimaryKey = col.IsPrimaryKey,
                    IsComputed = col.IsComputed,
                    IsRowVersion = col.IsRowVersion,
                    HasDefault = col.HasDefault,
                    IsUnique = col.IsUnique,
                    IsSequenceDefault = col.IsSequenceDefault,
                };

                if (col.IsIdentity || col.IsComputed || col.IsRowVersion || col.IsSequenceDefault)
                {
                    colPlan.Generator = "skip";
                }
                else if (fkColumnNames.Contains(col.Name))
                {
                    var fk = table.ForeignKeys.First(f =>
                        f.ParentColumn.Equals(col.Name, StringComparison.OrdinalIgnoreCase));

                    colPlan.Generator = "foreignKey";
                    colPlan.GeneratorArgs = new Dictionary<string, object?>
                    {
                        ["referencedTable"] = fk.FullReferencedTableName,
                        ["referencedColumn"] = fk.ReferencedColumn,
                        ["isSelfReferencing"] = fk.IsSelfReferencing,
                        ["compositeFkGroup"] = fk.FkName
                    };
                }
                else
                {
                    ResolveGenerator(col, colPlan);
                }

                tablePlan.Columns.Add(colPlan);
            }

            if (table.UniqueConstraints.Count > 0)
            {
                tablePlan.UniqueConstraints = table.UniqueConstraints
                    .Select(uc => new UniqueConstraintPlan
                    {
                        Name = uc.Name,
                        Columns = new List<string>(uc.Columns),
                        FilterDefinition = uc.FilterDefinition
                    })
                    .ToList();
            }

            plan.Tables.Add(tablePlan);
        }

        return plan;
    }

    public async Task WritePlanAsync(GenerationPlan plan, string outputPath)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();

        var yaml = serializer.Serialize(plan);
        await File.WriteAllTextAsync(outputPath, yaml);
    }

    public static async Task<GenerationPlan> ReadPlanAsync(string planPath)
    {
        var yaml = await File.ReadAllTextAsync(planPath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        return deserializer.Deserialize<GenerationPlan>(yaml)
               ?? throw new InvalidOperationException("Failed to deserialize plan file.");
    }

    private static void ResolveGenerator(ColumnInfo col, ColumnPlan colPlan)
    {
        if (IsUnsupportedType(col))
        {
            colPlan.Generator = "skip";
            return;
        }

        if (StringCompatibleTypes.Contains(col.SqlType))
        {
            var name = col.Name.ToLowerInvariant();

            foreach (var rule in NameHeuristics.Rules)
            {
                if (rule.Match(name))
                {
                    colPlan.Generator = rule.GeneratorName;
                    if (rule.Args != null)
                        colPlan.GeneratorArgs = new Dictionary<string, object?>(rule.Args);
                    return;
                }
            }
        }

        var sqlType = col.SqlType.ToLowerInvariant();
        if (SqlTypeMap.TryGetValue(sqlType, out var mapping))
        {
            colPlan.Generator = mapping.Generator;
            if (mapping.Args != null)
                colPlan.GeneratorArgs = new Dictionary<string, object?>(mapping.Args);
        }
        else
        {
            colPlan.Generator = "Random.AlphaNumeric";
            colPlan.GeneratorArgs = new Dictionary<string, object?> { ["length"] = 8 };
        }
    }

    internal static bool IsUnsupportedType(ColumnInfo col) =>
        UnsupportedTypes.Contains(col.SqlType)
        || (col.IsUserDefined && !col.SqlType.Equals("sql_variant", StringComparison.OrdinalIgnoreCase));
}
