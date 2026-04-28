using SyntheticDataGenerator.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SyntheticDataGenerator.Services;

public class PlanGenerator
{

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
        string mode = "insert",
        Dictionary<string, HashSet<string>>? columnsInScope = null,
        List<ExternalDependency>? externalDependencies = null,
        List<CustomDependencyGroup>? customDependencies = null)
    {
        var outboundFkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (externalDependencies is not null)
        {
            foreach (var dep in externalDependencies.Where(d =>
                         d.Direction.Equals("outbound", StringComparison.OrdinalIgnoreCase)))
                outboundFkKeys.Add($"{dep.ScopedTable}.{dep.ScopedColumn}");
        }

        var customDepLookup = BuildCustomDependencyLookup(customDependencies, sortedTables);

        var plan = new GenerationPlan
        {
            Mode = mode,
            Seed = seed,
            Locale = locale,
            Tables = [],
            ExternalDependencies = externalDependencies is { Count: > 0 } ? externalDependencies : null,
            CustomDependencies = customDependencies is { Count: > 0 } ? customDependencies : null
        };

        for (var i = 0; i < sortedTables.Count; i++)
        {
            var table = sortedTables[i];

            HashSet<string>? columnSet = null;
            if (columnsInScope is not null)
                columnsInScope.TryGetValue(table.FullName, out columnSet);

            var fkColumnNames = new HashSet<string>(
                table.ForeignKeys
                    .Where(fk => columnSet is null || columnSet.Contains(fk.ParentColumn))
                    .Select(fk => fk.ParentColumn),
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
                if (columnSet is not null && !col.IsPrimaryKey && !columnSet.Contains(col.Name))
                    continue;

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

                if (col.IsAutoGenerated)
                {
                    colPlan.Generator = "skip";
                }
                else if (fkColumnNames.Contains(col.Name))
                {
                    var fk = table.ForeignKeys.First(f =>
                        f.ParentColumn.Equals(col.Name, StringComparison.OrdinalIgnoreCase));

                    var isExternal = outboundFkKeys.Contains($"{table.FullName}.{col.Name}");

                    colPlan.Generator = "foreignKey";
                    colPlan.GeneratorArgs = new Dictionary<string, object?>
                    {
                        ["referencedTable"] = fk.FullReferencedTableName,
                        ["referencedColumn"] = fk.ReferencedColumn,
                        ["isSelfReferencing"] = fk.IsSelfReferencing,
                        ["compositeFkGroup"] = fk.FkName,
                        ["isExternal"] = isExternal
                    };
                }
                else if (customDepLookup.TryGetValue($"{table.FullName}.{col.Name}", out var source))
                {
                    colPlan.Generator = "customDependency";
                    colPlan.GeneratorArgs = new Dictionary<string, object?>
                    {
                        ["sourceTable"] = source.Table,
                        ["sourceColumn"] = source.Column,
                        ["isExternal"] = source.IsExternalRoot
                    };
                }
                else
                {
                    ResolveGenerator(col, colPlan);
                }

                tablePlan.Columns.Add(colPlan);
            }

            var constraints = columnSet is not null
                ? table.UniqueConstraints
                    .Where(uc => uc.Columns.All(c => columnSet.Contains(c)))
                    .ToList()
                : table.UniqueConstraints;

            if (constraints.Count > 0)
            {
                tablePlan.UniqueConstraints = constraints
                    .Select(uc => new UniqueConstraintPlan
                    {
                        Name = uc.Name,
                        Columns = new List<string>(uc.Columns),
                        FilterDefinition = uc.FilterDefinition
                    })
                    .ToList();
            }

            var maxRows = ComputeMaxDistinctRows(tablePlan, table);
            if (maxRows.HasValue && tablePlan.RowCount > maxRows.Value)
            {
                Console.WriteLine(
                    $"  WARNING: [{table.FullName}] rowCount capped from {tablePlan.RowCount} " +
                    $"to {maxRows.Value} (limited by narrow PK/unique column cardinality).");
                tablePlan.RowCount = maxRows.Value;
            }

            plan.Tables.Add(tablePlan);
        }

        return plan;
    }

    /// <summary>
    /// Builds a lookup from "schema.table.column" -> source CustomColumnRef for
    /// all dependent columns in custom dependency groups. The source is whichever
    /// column the validator flagged with <see cref="CustomColumnRef.IsSource"/>;
    /// every other column in the group becomes a dependent that copies from it.
    /// Dependents that are auto-generated ("skip") columns are excluded — the
    /// database fills them in, so the runtime should not overwrite them.
    /// </summary>
    private static Dictionary<string, CustomColumnRef> BuildCustomDependencyLookup(
        List<CustomDependencyGroup>? groups,
        List<TableInfo> sortedTables)
    {
        var lookup = new Dictionary<string, CustomColumnRef>(StringComparer.OrdinalIgnoreCase);
        if (groups is null)
            return lookup;

        var columnLookup = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in sortedTables)
        {
            foreach (var col in table.Columns)
                columnLookup.TryAdd($"{table.FullName}.{col.Name}", col);
        }

        foreach (var group in groups)
        {
            if (group.Columns.Count < 2)
                continue;

            var source = group.Columns.FirstOrDefault(c => c.IsSource)
                         ?? group.Columns[0];

            foreach (var dep in group.Columns)
            {
                if (ReferenceEquals(dep, source))
                    continue;

                var depColKey = $"{dep.Table}.{dep.Column}";
                var depIsSkip = columnLookup.TryGetValue(depColKey, out var depCol)
                                && IsSkipColumn(depCol);
                if (depIsSkip)
                    continue;

                lookup.TryAdd(depColKey, source);
            }
        }

        return lookup;
    }

    private static bool IsSkipColumn(ColumnInfo col) => col.IsAutoGenerated;

    public async Task WritePlanAsync(GenerationPlan plan, string outputPath)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
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

    /// <summary>
    /// Returns the maximum number of distinct rows this table can support, based on the
    /// narrowest PK or unique column/constraint. Returns null if no constraint limits it.
    /// </summary>
    internal static int? ComputeMaxDistinctRows(TablePlan tablePlan, TableInfo table)
    {
        long? minCardinality = null;

        var pkColumns = tablePlan.Columns
            .Where(c => c.IsPrimaryKey && !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pkColumns.Count > 0)
        {
            long compositeCardinality = 1;
            foreach (var col in pkColumns)
            {
                var card = MaxCardinalityForColumn(col);
                if (card.HasValue)
                    compositeCardinality = Math.Min(compositeCardinality * card.Value, long.MaxValue);
                else
                {
                    compositeCardinality = long.MaxValue;
                    break;
                }
            }

            if (compositeCardinality < long.MaxValue)
                minCardinality = compositeCardinality;
        }

        foreach (var col in tablePlan.Columns.Where(c =>
                     c.IsUnique && !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase)))
        {
            var card = MaxCardinalityForColumn(col);
            if (card.HasValue)
                minCardinality = minCardinality.HasValue
                    ? Math.Min(minCardinality.Value, card.Value)
                    : card.Value;
        }

        if (tablePlan.UniqueConstraints is { Count: > 0 })
        {
            foreach (var uc in tablePlan.UniqueConstraints)
            {
                long compositeCard = 1;
                var allKnown = true;
                foreach (var colName in uc.Columns)
                {
                    var col = tablePlan.Columns.FirstOrDefault(c =>
                        c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                    if (col is null || col.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase))
                    {
                        allKnown = false;
                        break;
                    }

                    var card = MaxCardinalityForColumn(col);
                    if (card.HasValue)
                        compositeCard = Math.Min(compositeCard * card.Value, long.MaxValue);
                    else
                    {
                        allKnown = false;
                        break;
                    }
                }

                if (allKnown && compositeCard < long.MaxValue)
                    minCardinality = minCardinality.HasValue
                        ? Math.Min(minCardinality.Value, compositeCard)
                        : compositeCard;
            }
        }

        if (minCardinality is > 0 and <= int.MaxValue)
            return (int)minCardinality.Value;

        return null;
    }

    internal static long? MaxCardinalityForColumn(ColumnPlan col)
    {
        var sqlType = col.SqlType.ToLowerInvariant();
        var effectiveLen = col.MaxLength;
        if (sqlType.StartsWith('n') && effectiveLen > 0)
            effectiveLen /= 2;
        if (effectiveLen <= 0)
            return null;

        const int alphaNumericChars = 36; // 0-9, a-z

        return sqlType switch
        {
            "char" or "nchar" when col.Generator == "Random.AlphaNumeric"
                => (long)Math.Pow(alphaNumericChars, effectiveLen),

            "bit" => 2,
            "tinyint" => 256,

            "char" or "nchar" => (long)Math.Pow(alphaNumericChars, effectiveLen),

            _ => null
        };
    }

    private static void ResolveGenerator(ColumnInfo col, ColumnPlan colPlan)
    {
        if (SqlTypeInfo.IsUnsupportedType(col))
        {
            colPlan.Generator = "skip";
            return;
        }

        if (SqlTypeInfo.StringCompatibleTypes.Contains(col.SqlType))
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
        SqlTypeInfo.IsUnsupportedType(col);
}
