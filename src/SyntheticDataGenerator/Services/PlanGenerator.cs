using System.Text.Json;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class PlanGenerator
{
    private static readonly (Func<string, bool> Match, string Generator, Dictionary<string, object?>? Args)[] NameRules =
    [
        (n => Like(n, "first") && Like(n, "name"), "Name.FirstName", null),
        (n => Like(n, "last") && Like(n, "name"),  "Name.LastName", null),
        (n => Like(n, "email"),                     "Internet.Email", null),
        (n => Like(n, "phone"),                     "Phone.PhoneNumber", new() { ["format"] = "###-###-####" }),
        (n => Like(n, "street") || (Like(n, "address") && !Like(n, "email")),
                                                    "Address.StreetAddress", null),
        (n => Like(n, "city"),                      "Address.City", null),
        (n => Like(n, "state"),                     "Address.StateAbbr", null),
        (n => Like(n, "zip") || Like(n, "postal"),  "Address.ZipCode", null),
        (n => Like(n, "country"),                   "Address.Country", null),
        (n => Like(n, "url") || Like(n, "website"), "Internet.Url", null),
        (n => Like(n, "description") || Like(n, "comment") || Like(n, "note"),
                                                    "Lorem.Sentence", null),
        (n => Like(n, "price") || Like(n, "amount") || Like(n, "cost") || Like(n, "salary"),
                                                    "Finance.Amount", new() { ["min"] = 1m, ["max"] = 10000m }),
        (n => Like(n, "company"),                   "Company.CompanyName", null),
        (n => Like(n, "title"),                     "Name.JobTitle", null),
        (n => Like(n, "quantity") || Like(n, "count") || Like(n, "qty"),
                                                    "Random.Int", new() { ["min"] = 1, ["max"] = 100 }),
        (n => Like(n, "status"),                    "PickRandom", new() { ["values"] = new[] { "Active", "Inactive", "Pending" } }),
        (n => n.StartsWith("is_", StringComparison.OrdinalIgnoreCase)
           || n.StartsWith("has_", StringComparison.OrdinalIgnoreCase),
                                                    "Random.Bool", null),
        (n => Like(n, "username") || Like(n, "user_name"),
                                                    "Internet.UserName", null),
        (n => Like(n, "password") || Like(n, "hash"),
                                                    "Internet.Password", null),
        (n => Like(n, "image") || Like(n, "avatar") || Like(n, "photo"),
                                                    "Internet.Avatar", null),
        (n => Like(n, "name"),                      "Name.FullName", null),
    ];

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
    };

    public GenerationPlan Generate(
        List<TableInfo> sortedTables,
        IReadOnlySet<string> selfReferencingTables,
        int defaultRowCount,
        int? seed,
        string locale = "en")
    {
        var plan = new GenerationPlan
        {
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
                Schema = table.Schema,
                Table = table.TableName,
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
                };

                if (col.IsIdentity || col.IsComputed)
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
                        ["referencedSchema"] = fk.ReferencedSchema,
                        ["referencedTable"] = fk.ReferencedTable,
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

            plan.Tables.Add(tablePlan);
        }

        return plan;
    }

    public async Task WritePlanAsync(GenerationPlan plan, string outputPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, plan, options);
    }

    public static async Task<GenerationPlan> ReadPlanAsync(string planPath)
    {
        await using var stream = File.OpenRead(planPath);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        return await JsonSerializer.DeserializeAsync<GenerationPlan>(stream, options)
               ?? throw new InvalidOperationException("Failed to deserialize plan file.");
    }

    private static void ResolveGenerator(ColumnInfo col, ColumnPlan colPlan)
    {
        if (!col.SqlType.Equals("bit", StringComparison.OrdinalIgnoreCase))
        {
            var name = col.Name.ToLowerInvariant();

            foreach (var (match, generator, args) in NameRules)
            {
                if (match(name))
                {
                    colPlan.Generator = generator;
                    if (args != null)
                        colPlan.GeneratorArgs = new Dictionary<string, object?>(args);
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

    private static bool Like(string input, string fragment) =>
        input.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
