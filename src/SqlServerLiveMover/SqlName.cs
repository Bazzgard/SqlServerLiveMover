using System.Text.RegularExpressions;

namespace SqlServerLiveMover;

internal sealed record SqlName(string Schema, string Table)
{
    private static readonly Regex SafePart = new(@"^[\p{L}_][\p{L}\p{N}_@$#]*$", RegexOptions.Compiled);

    public string Quoted => $"{Quote(Schema)}.{Quote(Table)}";
    public string Canonical => $"{Schema}.{Table}";

    public static SqlName Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ConfigException("テーブル名は必須です。");
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        var result = parts.Length switch
        {
            1 => new SqlName("dbo", parts[0]),
            2 => new SqlName(parts[0], parts[1]),
            _ => throw new ConfigException($"テーブル名は schema.table 形式で指定してください: {value}")
        };
        if (!SafePart.IsMatch(result.Schema) || !SafePart.IsMatch(result.Table))
            throw new ConfigException($"テーブル名に使用できない文字があります: {value}");
        return result;
    }

    public static string Quote(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}
