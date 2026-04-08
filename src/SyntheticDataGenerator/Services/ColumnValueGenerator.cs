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
            ["Phone.PhoneNumber"]    = (f, a) => f.Phone.PhoneNumber(GetString(a, "format", "###-###-####")),
            ["Address.StreetAddress"] = (f, _) => f.Address.StreetAddress(),
            ["Address.City"]         = (f, _) => f.Address.City(),
            ["Address.StateAbbr"]    = (f, _) => f.Address.StateAbbr(),
            ["Address.ZipCode"]      = (f, _) => f.Address.ZipCode(),
            ["Address.Country"]      = (f, _) => f.Address.Country(),
            ["Lorem.Word"]           = (f, _) => f.Lorem.Word(),
            ["Lorem.Sentence"]       = (f, _) => f.Lorem.Sentence(),
            ["Finance.Amount"]       = (f, a) => (object)f.Finance.Amount(GetDecimal(a, "min", 1), GetDecimal(a, "max", 10000)),
            ["Company.CompanyName"]  = (f, _) => f.Company.CompanyName(),
            ["Random.Int"]           = (f, a) => (object)f.Random.Int(GetInt(a, "min", 1), GetInt(a, "max", int.MaxValue / 2)),
            ["Random.Long"]          = (f, a) => (object)f.Random.Long(GetLong(a, "min", 1), GetLong(a, "max", long.MaxValue / 2)),
            ["Random.Short"]         = (f, a) => (object)f.Random.Short((short)GetInt(a, "min", 1), (short)GetInt(a, "max", short.MaxValue)),
            ["Random.Byte"]          = (f, _) => (object)f.Random.Byte(),
            ["Random.Bool"]          = (f, _) => (object)f.Random.Bool(),
            ["Random.Decimal"]       = (f, a) => (object)f.Random.Decimal(GetDecimal(a, "min", 0), GetDecimal(a, "max", 99999)),
            ["Random.Double"]        = (f, a) => (object)f.Random.Double(GetDouble(a, "min", 0), GetDouble(a, "max", 99999)),
            ["Random.Float"]         = (f, a) => (object)(float)f.Random.Double(GetDouble(a, "min", 0), GetDouble(a, "max", 99999)),
            ["Random.AlphaNumeric"]  = (f, a) => f.Random.AlphaNumeric(GetInt(a, "length", 8)),
            ["Random.Bytes"]         = (f, a) => f.Random.Bytes(GetInt(a, "count", 16)),
            ["Date.Past"]            = (f, a) => f.Date.Past(GetInt(a, "yearsToGoBack", 5)),
            ["Date.PastDateOnly"]    = (f, a) => DateOnly.FromDateTime(f.Date.Past(GetInt(a, "yearsToGoBack", 5))),
            ["Date.Timespan"]        = (f, _) => TimeOnly.FromTimeSpan(ClampTimeSpan(f.Date.Timespan())),
            ["Date.PastOffset"]      = (f, a) => f.Date.PastOffset(GetInt(a, "yearsToGoBack", 5)),
            ["Guid"]                 = (_, _) => Guid.NewGuid(),
            ["null"]                 = (_, _) => null,
            ["PickRandom"]           = (f, a) => PickRandomFromArgs(f, a),
            ["Random.SqlVariant"]    = (f, _) => GenerateSqlVariantValue(f),
        };

    private static readonly (Func<string, bool> Match, Func<Faker, ColumnInfo, object> Generate)[] NameRules =
    [
        (n => Like(n, "first") && Like(n, "name"), (f, _) => f.Name.FirstName()),
        (n => Like(n, "last") && Like(n, "name"),  (f, _) => f.Name.LastName()),
        (n => Like(n, "email"),                     (f, _) => f.Internet.Email()),
        (n => Like(n, "phone"),                     (f, _) => f.Phone.PhoneNumber("###-###-####")),
        (n => Like(n, "street") || (Like(n, "address") && !Like(n, "email")),
                                                    (f, _) => f.Address.StreetAddress()),
        (n => Like(n, "city"),                      (f, _) => f.Address.City()),
        (n => Like(n, "state"),                     (f, _) => f.Address.StateAbbr()),
        (n => Like(n, "zip") || Like(n, "postal"),  (f, _) => f.Address.ZipCode()),
        (n => Like(n, "country"),                   (f, _) => f.Address.Country()),
        (n => Like(n, "url") || Like(n, "website"), (f, _) => f.Internet.Url()),
        (n => Like(n, "description") || Like(n, "comment") || Like(n, "note"),
                                                    (f, _) => f.Lorem.Sentence()),
        (n => Like(n, "price") || Like(n, "amount") || Like(n, "cost") || Like(n, "salary"),
                                                    (f, _) => (object)f.Finance.Amount(1, 10000)),
        (n => Like(n, "company"),                   (f, _) => f.Company.CompanyName()),
        (n => Like(n, "title"),                     (f, _) => f.Name.JobTitle()),
        (n => Like(n, "quantity") || Like(n, "count") || Like(n, "qty"),
                                                    (f, _) => (object)f.Random.Int(1, 100)),
        (n => Like(n, "status"),                    (f, _) => f.PickRandom("Active", "Inactive", "Pending")),
        (n => n.StartsWith("is_", StringComparison.OrdinalIgnoreCase)
           || n.StartsWith("has_", StringComparison.OrdinalIgnoreCase),
                                                    (f, _) => (object)f.Random.Bool()),
        (n => Like(n, "username") || Like(n, "user_name"),
                                                    (f, _) => f.Internet.UserName()),
        (n => Like(n, "password") || Like(n, "hash"),
                                                    (f, _) => f.Internet.Password()),
        (n => Like(n, "image") || Like(n, "avatar") || Like(n, "photo"),
                                                    (f, _) => f.Internet.Avatar()),
        (n => Like(n, "name"),                      (f, _) => f.Name.FullName()),
    ];

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

        if (!string.IsNullOrWhiteSpace(plan.ValuesFile))
        {
            var values = LoadValuesFile(plan.ValuesFile, plan.Name);
            var picked = _faker.PickRandom(values);
            return ClampToColumnPlan(picked, plan);
        }

        if (Generators.TryGetValue(plan.Generator, out var generator))
        {
            var value = generator(_faker, plan.GeneratorArgs);

            if (string.Equals(plan.Generator, "Lorem.Word", StringComparison.OrdinalIgnoreCase)
                && plan.GeneratorArgs.TryGetValue("wrapXml", out var wrapXml)
                && IsTruthy(wrapXml))
            {
                value = $"<data>{value}</data>";
            }

            return ClampToColumnPlan(value, plan);
        }

        return _faker.Random.AlphaNumeric(8);
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
            foreach (var (match, generate) in NameRules)
            {
                if (!match(name)) continue;
                var value = generate(_faker, column);
                if (value is decimal or int or long or short or byte or float or double)
                    return ClampToColumn(value, column);
                break;
            }
            return GenerateByType(column);
        }

        if (TypeFirstTypes.Contains(column.SqlType))
            return GenerateByType(column);

        var name2 = column.Name.ToLowerInvariant();

        foreach (var (match, generate) in NameRules)
        {
            if (match(name2))
                return ClampToColumn(generate(_faker, column), column);
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

    private static object? ClampToColumnPlan(object? value, ColumnPlan plan)
    {
        if (value is null) return null;

        if (value is string s)
        {
            var maxLen = EffectiveLengthFromPlan(plan);
            if (maxLen > 0 && s.Length > maxLen)
                return s[..maxLen];
        }

        if (value is decimal d)
        {
            var sqlType = plan.SqlType.ToLowerInvariant();
            if (sqlType is "money" or "smallmoney" or "decimal" or "numeric")
            {
                return ClampDecimalToPrecision(d, plan.Precision, plan.Scale);
            }
        }

        return value;
    }

    private static object ClampToColumn(object value, ColumnInfo column)
    {
        if (value is string s)
        {
            var maxLen = EffectiveLength(column);
            if (maxLen > 0 && s.Length > maxLen)
                return s[..maxLen];
        }

        if (value is decimal d)
        {
            if (column.SqlType.Equals("money", StringComparison.OrdinalIgnoreCase) ||
                column.SqlType.Equals("smallmoney", StringComparison.OrdinalIgnoreCase) ||
                column.SqlType.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
                column.SqlType.Equals("numeric", StringComparison.OrdinalIgnoreCase))
            {
                return ClampDecimalToPrecision(d, column.Precision, column.Scale);
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

    private static int EffectiveLengthFromPlan(ColumnPlan plan)
    {
        if (plan.MaxLength <= 0) return 50;

        var type = plan.SqlType.ToLowerInvariant();
        if (type.StartsWith('n'))
            return plan.MaxLength / 2;

        return plan.MaxLength;
    }

    private static int EffectiveLength(ColumnInfo column)
    {
        if (column.MaxLength <= 0) return 50;

        var type = column.SqlType.ToLowerInvariant();
        if (type.StartsWith('n'))
            return column.MaxLength / 2;

        return column.MaxLength;
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

    private static bool Like(string input, string fragment) =>
        input.Contains(fragment, StringComparison.OrdinalIgnoreCase);

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

    private static bool IsTruthy(object? value)
    {
        if (value is bool b) return b;
        if (value is string str) return str.Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static int GetInt(Dictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is int i) return i;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    private static long GetLong(Dictionary<string, object?> args, string key, long defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is long l) return l;
        if (value is int i) return i;
        return long.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    private static decimal GetDecimal(Dictionary<string, object?> args, string key, decimal defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is decimal d) return d;
        if (value is int i) return i;
        if (value is double dbl) return (decimal)dbl;
        return decimal.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    private static double GetDouble(Dictionary<string, object?> args, string key, double defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is double d) return d;
        if (value is int i) return i;
        if (value is decimal dec) return (double)dec;
        return double.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    private static string GetString(Dictionary<string, object?> args, string key, string defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is string s) return s;
        return value.ToString() ?? defaultValue;
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
