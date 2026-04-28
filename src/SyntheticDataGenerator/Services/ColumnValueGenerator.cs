using Bogus;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class ColumnValueGenerator
{
    private readonly Faker _faker;
    private string? _planBasePath;
    private readonly Dictionary<string, string[]> _valuesFileCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Func<Faker, Dictionary<string, object?>, object?>> Generators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Name.FirstName"]       = (f, _) => f.Name.FirstName(),
            ["Name.LastName"]        = (f, _) => f.Name.LastName(),
            ["Name.FullName"]        = (f, _) => f.Name.FullName(),
            ["Name.JobTitle"]        = (f, _) => f.Name.JobTitle(),
            ["Internet.Email"]       = (f, _) => f.Internet.Email(),
            ["Internet.UserName"]    = (f, _) => f.Internet.UserName(),
            ["Internet.Password"]    = (f, _) => f.Internet.Password(),
            ["Internet.Url"]         = (f, _) => f.Internet.Url(),
            ["Internet.Avatar"]      = (f, _) => f.Internet.Avatar(),
            ["Phone.PhoneNumber"]    = (f, a) => f.Phone.PhoneNumber(Helpers.GetString(a, "format", "###-###-####")),
            ["Address.StreetAddress"] = (f, _) => f.Address.StreetAddress(),
            ["Address.City"]         = (f, _) => f.Address.City(),
            ["Address.StateAbbr"]    = (f, _) => f.Address.StateAbbr(),
            ["Address.ZipCode"]      = (f, _) => f.Address.ZipCode(),
            ["Address.Country"]      = (f, _) => f.Address.Country(),
            ["Lorem.Word"]           = (f, _) => f.Lorem.Word(),
            ["Lorem.Sentence"]       = (f, _) => f.Lorem.Sentence(),
            ["Finance.Amount"]       = (f, a) => (object)f.Finance.Amount(Helpers.GetDecimal(a, "min", 1), Helpers.GetDecimal(a, "max", 10000)),
            ["Company.CompanyName"]  = (f, _) => f.Company.CompanyName(),
            ["Random.Int"]           = (f, a) => (object)f.Random.Int(Helpers.GetInt(a, "min", 1), Helpers.GetInt(a, "max", int.MaxValue / 2)),
            ["Random.Long"]          = (f, a) => (object)f.Random.Long(Helpers.GetLong(a, "min", 1), Helpers.GetLong(a, "max", long.MaxValue / 2)),
            ["Random.Short"]         = (f, a) => (object)f.Random.Short((short)Helpers.GetInt(a, "min", 1), (short)Helpers.GetInt(a, "max", short.MaxValue)),
            ["Random.Byte"]          = (f, _) => (object)f.Random.Byte(),
            ["Random.Bool"]          = (f, _) => (object)f.Random.Bool(),
            ["Random.Decimal"]       = (f, a) => (object)f.Random.Decimal(Helpers.GetDecimal(a, "min", 0), Helpers.GetDecimal(a, "max", 99999)),
            ["Random.Double"]        = (f, a) => (object)f.Random.Double(Helpers.GetDouble(a, "min", 0), Helpers.GetDouble(a, "max", 99999)),
            ["Random.Float"]         = (f, a) => (object)(float)f.Random.Double(Helpers.GetDouble(a, "min", 0), Helpers.GetDouble(a, "max", 99999)),
            ["Random.AlphaNumeric"]  = (f, a) => f.Random.AlphaNumeric(Helpers.GetInt(a, "length", 8)),
            ["Random.Bytes"]         = (f, a) => f.Random.Bytes(Helpers.GetInt(a, "count", 16)),
            ["Date.Past"]            = (f, a) => f.Date.Past(Helpers.GetInt(a, "yearsToGoBack", 5)),
            ["Date.PastDateOnly"]    = (f, a) => DateOnly.FromDateTime(f.Date.Past(Helpers.GetInt(a, "yearsToGoBack", 5))),
            ["Date.Timespan"]        = (f, _) => TimeOnly.FromTimeSpan(ClampTimeSpan(f.Date.Timespan())),
            ["Date.PastOffset"]      = (f, a) => f.Date.PastOffset(Helpers.GetInt(a, "yearsToGoBack", 5)),
            ["Guid"]                 = (_, _) => Guid.NewGuid(),
            ["null"]                 = (_, _) => null,
            ["PickRandom"]           = (f, a) => PickRandomFromArgs(f, a),
            ["Random.SqlVariant"]    = (f, _) => GenerateSqlVariantValue(f),
        };

    public ColumnValueGenerator(int? seed = null, string locale = "en")
    {
        _faker = seed.HasValue
            ? new Faker(locale) { Random = new Randomizer(seed.Value) }
            : new Faker(locale);
    }

    public void SetPlanBasePath(string basePath) => _planBasePath = basePath;

    public object? GenerateFromPlan(ColumnPlan plan)
    {
        if (string.Equals(plan.Generator, "skip", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(plan.Generator, "foreignKey", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(plan.Generator, "customDependency", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(plan.Generator, "valueList", StringComparison.OrdinalIgnoreCase))
        {
            var picked = PickFromValueListArgs(plan);
            if (picked is not null)
                return ClampValue(picked, plan);
            throw new InvalidOperationException(
                $"Column '{plan.Name}' uses generator 'valueList' but neither 'valuesFile' " +
                "nor a non-empty 'values' arg is set.");
        }

        if (!string.IsNullOrWhiteSpace(plan.ValuesFile))
        {
            var values = LoadValuesFile(plan.ValuesFile, plan.Name);
            var picked = _faker.PickRandom(values);
            return ClampValue(picked, plan);
        }

        if (Generators.TryGetValue(plan.Generator, out var generator))
        {
            var value = generator(_faker, plan.GeneratorArgs);

            if (string.Equals(plan.Generator, "Lorem.Word", StringComparison.OrdinalIgnoreCase)
                && plan.GeneratorArgs.TryGetValue("wrapXml", out var wrapXml)
                && Helpers.IsTruthy(wrapXml))
            {
                value = $"<data>{value}</data>";
            }

            return ClampValue(value, plan);
        }

        return _faker.Random.AlphaNumeric(8);
    }

    /// <summary>
    /// Picks a random value for a "valueList" column. Honors either a
    /// <c>valuesFile</c> path (loaded via <see cref="LoadValuesFile"/>) or an
    /// inline <c>values</c> arg. The inline list may have arrived as
    /// <c>List&lt;string&gt;</c> (in-memory plan) or <c>List&lt;object?&gt;</c>
    /// (after a YamlDotNet round-trip), so both shapes are normalized.
    /// </summary>
    private object? PickFromValueListArgs(ColumnPlan plan)
    {
        if (plan.GeneratorArgs.TryGetValue("valuesFile", out var vfObj)
            && vfObj is string vf
            && !string.IsNullOrWhiteSpace(vf))
        {
            var values = LoadValuesFile(vf, plan.Name);
            return _faker.PickRandom(values);
        }

        if (!string.IsNullOrWhiteSpace(plan.ValuesFile))
        {
            var values = LoadValuesFile(plan.ValuesFile, plan.Name);
            return _faker.PickRandom(values);
        }

        if (plan.GeneratorArgs.TryGetValue("values", out var vObj) && vObj is not null)
        {
            var inline = NormalizeInlineValues(vObj);
            if (inline.Length > 0)
                return _faker.PickRandom(inline);
        }

        return null;
    }

    private static string[] NormalizeInlineValues(object raw)
    {
        return raw switch
        {
            IEnumerable<string> ss => ss.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
            IEnumerable<object?> os => os
                .Where(o => o is not null)
                .Select(o => o!.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray(),
            _ => []
        };
    }

    private string[] LoadValuesFile(string filePath, string columnName)
    {
        var resolvedPath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(filePath, _planBasePath ?? Directory.GetCurrentDirectory());

        if (_valuesFileCache.TryGetValue(resolvedPath, out var cached))
            return cached;

        if (!File.Exists(resolvedPath))
            throw new InvalidOperationException(
                $"Values file '{resolvedPath}' for column '{columnName}' does not exist.");

        var values = File.ReadAllLines(resolvedPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (values.Length == 0)
            throw new InvalidOperationException(
                $"Values file '{resolvedPath}' for column '{columnName}' is empty.");

        _valuesFileCache[resolvedPath] = values;
        return values;
    }

    private static readonly HashSet<string> TypeFirstTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "binary", "varbinary", "image", "timestamp", "rowversion",
        "bit",
        "int", "bigint", "smallint", "tinyint",
        "float", "real",
        "datetime", "datetime2", "smalldatetime", "date", "time", "datetimeoffset",
        "uniqueidentifier",
        "sql_variant",
        "geography", "geometry", "hierarchyid",
    };

    private static readonly HashSet<string> NumericTypesWithNameHeuristics = new(StringComparer.OrdinalIgnoreCase)
    {
        "decimal", "numeric", "money", "smallmoney",
    };

    public object? Generate(ColumnInfo column)
    {
        if (NumericTypesWithNameHeuristics.Contains(column.SqlType))
        {
            var name = column.Name.ToLowerInvariant();
            foreach (var rule in NameHeuristics.Rules)
            {
                if (!rule.Match(name)) continue;
                if (Generators.TryGetValue(rule.GeneratorName, out var gen))
                {
                    var args = rule.Args ?? new Dictionary<string, object?>();
                    var value = gen(_faker, args);
                    if (value is decimal or int or long or short or byte or float or double)
                        return ClampValue(value, column);
                }
                break;
            }
            return GenerateByType(column);
        }

        if (TypeFirstTypes.Contains(column.SqlType))
            return GenerateByType(column);

        var name2 = column.Name.ToLowerInvariant();

        foreach (var rule in NameHeuristics.Rules)
        {
            if (!rule.Match(name2)) continue;
            if (Generators.TryGetValue(rule.GeneratorName, out var gen))
            {
                var args = rule.Args ?? new Dictionary<string, object?>();
                return ClampValue(gen(_faker, args), column);
            }
            return GenerateByType(column);
        }

        return GenerateByType(column);
    }

    private object? GenerateByType(ColumnInfo column)
    {
        var type = column.SqlType.ToLowerInvariant();

        return type switch
        {
            "int"                => _faker.Random.Int(1, int.MaxValue / 2),
            "bigint"             => _faker.Random.Long(1, long.MaxValue / 2),
            "smallint"           => _faker.Random.Short(1, short.MaxValue),
            "tinyint"            => _faker.Random.Byte(),
            "bit"                => _faker.Random.Bool(),
            "decimal" or "numeric" => GenerateDecimalForColumn(column),
            "money" or "smallmoney" => GenerateMoneyForColumn(column),
            "float"              => _faker.Random.Double(0, 99999),
            "real"               => (float)_faker.Random.Double(0, 99999),
            "datetime" or "datetime2" or "smalldatetime"
                                 => _faker.Date.Past(5),
            "date"               => DateOnly.FromDateTime(_faker.Date.Past(5)),
            "time"               => TimeOnly.FromTimeSpan(ClampTimeSpan(_faker.Date.Timespan())),
            "datetimeoffset"     => _faker.Date.PastOffset(5),
            "char" or "nchar"    => TruncateString(_faker.Random.AlphaNumeric(Math.Max(1, EffectiveLength(column))),
                                        EffectiveLength(column)),
            "varchar" or "nvarchar"
                                 => TruncateString(_faker.Lorem.Word(), EffectiveLength(column)),
            "text" or "ntext"    => _faker.Lorem.Sentence(),
            "uniqueidentifier"   => Guid.NewGuid(),
            "varbinary" or "binary" or "image"
                                 => _faker.Random.Bytes(Math.Min(16, Math.Max(1, column.MaxLength > 0 ? column.MaxLength : 16))),
            "xml"                => $"<data>{_faker.Lorem.Word()}</data>",
            "sql_variant"        => GenerateSqlVariantValue(_faker),
            "geography" or "geometry" or "hierarchyid"
                                 => null,
            _                    => _faker.Random.AlphaNumeric(8)
        };
    }

    private static object? ClampValue(object? value, IColumnMetadata meta)
    {
        if (value is null) return null;

        if (value is string s)
        {
            var maxLen = EffectiveLength(meta);
            if (maxLen > 0 && s.Length > maxLen)
                return s[..maxLen];
        }

        if (value is decimal d)
        {
            var sqlType = meta.SqlType.ToLowerInvariant();
            if (sqlType is "money" or "smallmoney" or "decimal" or "numeric")
            {
                return ClampDecimalToPrecision(d, meta.Precision, meta.Scale);
            }
        }

        return value;
    }

    private decimal GenerateDecimalForColumn(ColumnInfo column)
    {
        var max = MaxDecimalForPrecisionScale(column.Precision, column.Scale);
        var scale = Math.Min(column.Scale, (byte)4);
        return Math.Round(_faker.Random.Decimal(0, max), scale);
    }

    private decimal GenerateMoneyForColumn(ColumnInfo column)
    {
        var sqlType = column.SqlType.ToLowerInvariant();
        decimal max = sqlType == "smallmoney" ? 214748m : 10000m;
        if (column.Precision > 0)
            max = Math.Min(max, MaxDecimalForPrecisionScale(column.Precision, column.Scale));
        return _faker.Finance.Amount(1, max);
    }

    private static decimal MaxDecimalForPrecisionScale(byte precision, byte scale)
    {
        if (precision == 0) return 99999m;
        var integerDigits = precision - scale;
        if (integerDigits <= 0) return 0.9m;
        var max = (decimal)Math.Pow(10, integerDigits) - 1;
        return Math.Max(max, 1m);
    }

    private static decimal ClampDecimalToPrecision(decimal value, byte precision, byte scale)
    {
        if (precision == 0) return value;
        var scaleClamped = Math.Round(value, Math.Min(scale, (byte)4));
        var max = MaxDecimalForPrecisionScale(precision, scale);
        if (scaleClamped > max) return max;
        if (scaleClamped < -max) return -max;
        return scaleClamped;
    }

    private static int EffectiveLength(IColumnMetadata meta)
    {
        if (meta.MaxLength <= 0) return 50;

        var type = meta.SqlType.ToLowerInvariant();
        if (type.StartsWith('n'))
            return meta.MaxLength / 2;

        return meta.MaxLength;
    }

    private static string TruncateString(string value, int maxLen)
    {
        if (maxLen <= 0) return value;
        return value.Length > maxLen ? value[..maxLen] : value;
    }

    private static TimeSpan ClampTimeSpan(TimeSpan ts)
    {
        var ticks = ts.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return new TimeSpan(ticks);
    }

    private static object PickRandomFromArgs(Faker f, Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("values", out var valuesObj) || valuesObj is null)
            return f.Lorem.Word();

        string[] values;
        if (valuesObj is string[] strArray)
        {
            values = strArray;
        }
        else if (valuesObj is object[] objArray)
        {
            values = objArray.Select(o => o?.ToString() ?? string.Empty).ToArray();
        }
        else if (valuesObj is IEnumerable<object> enumerable)
        {
            values = enumerable.Select(o => o?.ToString() ?? string.Empty).ToArray();
        }
        else
        {
            return f.Lorem.Word();
        }

        return values.Length > 0 ? f.PickRandom(values) : f.Lorem.Word();
    }

    private static object GenerateSqlVariantValue(Faker f)
    {
        return f.Random.Int(0, 4) switch
        {
            0 => (object)f.Random.Int(1, int.MaxValue / 2),
            1 => f.Lorem.Word(),
            2 => f.Date.Past(5),
            3 => f.Random.Double(0, 99999),
            _ => (object)f.Random.Decimal(0, 99999m),
        };
    }
}
