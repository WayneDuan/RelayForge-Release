namespace RelayForge.Panel.Api;

public static class DbValue
{
    public static string String(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value) ?? "" : "";
    public static long Long(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;
    public static int Int(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToInt32(value) : 0;
    public static int? NullableInt(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToInt32(value) : null;
    public static decimal Decimal(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToDecimal(value) : 0m;
    public static object? Value(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value : null;
}
