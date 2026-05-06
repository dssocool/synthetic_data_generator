namespace SyntheticDataGenerator.Services;

internal static class Helpers
{
    internal static bool IsTruthy(object? value)
    {
        if (value is bool b) return b;
        if (value is string str) return str.Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    internal static bool Like(string input, string fragment) =>
        input.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    internal static string GetArgString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return string.Empty;
        if (value is string s) return s;
        return value.ToString() ?? string.Empty;
    }

    internal static int GetInt(Dictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is int i) return i;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    internal static long GetLong(Dictionary<string, object?> args, string key, long defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is long l) return l;
        if (value is int i) return i;
        return long.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    internal static decimal GetDecimal(Dictionary<string, object?> args, string key, decimal defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is decimal d) return d;
        if (value is int i) return i;
        if (value is double dbl) return (decimal)dbl;
        return decimal.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    internal static double GetDouble(Dictionary<string, object?> args, string key, double defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is double d) return d;
        if (value is int i) return i;
        if (value is decimal dec) return (double)dec;
        return double.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    internal static string GetString(Dictionary<string, object?> args, string key, string defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        if (value is string s) return s;
        return value.ToString() ?? defaultValue;
    }
}
