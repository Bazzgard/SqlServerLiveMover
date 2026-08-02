using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlServerLiveMover;

internal sealed class AppConfig
{
    public string SourceConnectionString { get; init; } = "";
    public string TargetConnectionString { get; init; } = "";
    public int BatchSize { get; init; } = 5_000;
    public int CommandTimeoutSeconds { get; init; } = 120;
    public string SourceConsistency { get; init; } = "snapshot";
    public List<TableConfig> Tables { get; init; } = [];

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ConfigException($"設定ファイルが見つかりません: {path}");

        return Parse(File.ReadAllText(path));
    }

    internal static AppConfig Parse(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var config = JsonSerializer.Deserialize<AppConfig>(json, options)
            ?? throw new ConfigException("設定ファイルを読み取れませんでした。");

        config = new AppConfig
        {
            SourceConnectionString = ExpandEnvironmentVariables(config.SourceConnectionString),
            TargetConnectionString = ExpandEnvironmentVariables(config.TargetConnectionString),
            BatchSize = config.BatchSize,
            CommandTimeoutSeconds = config.CommandTimeoutSeconds,
            SourceConsistency = config.SourceConsistency,
            Tables = config.Tables
        };
        config.Validate();
        return config;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceConnectionString))
            throw new ConfigException("sourceConnectionString は必須です。");
        if (string.IsNullOrWhiteSpace(TargetConnectionString))
            throw new ConfigException("targetConnectionString は必須です。");
        if (BatchSize is < 1 or > 1_000_000)
            throw new ConfigException("batchSize は1～1,000,000の範囲で指定してください。");
        if (CommandTimeoutSeconds < 1)
            throw new ConfigException("commandTimeoutSeconds は1以上にしてください。");
        if (SourceConsistency is not ("snapshot" or "locked" or "readCommitted"))
            throw new ConfigException("sourceConsistency は snapshot、locked、readCommitted のいずれかです。");
        if (Tables.Count == 0)
            throw new ConfigException("tables を1件以上指定してください。");

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in Tables)
        {
            _ = SqlName.Parse(table.Source);
            _ = SqlName.Parse(table.EffectiveTarget);
            if (!targets.Add(table.EffectiveTarget))
                throw new ConfigException($"移行先テーブルが重複しています: {table.EffectiveTarget}");
            if (table.Columns is { Count: 0 })
                throw new ConfigException($"columns は省略するか1件以上指定してください: {table.Source}");
            if (table.Columns is not null &&
                table.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != table.Columns.Count)
                throw new ConfigException($"columnsに重複があります: {table.Source}");
            if (table.EffectiveOperationMode is not ("upsertKeep" or "upsertDelete" or "emptyOnly" or "replace"))
                throw new ConfigException(
                    $"operationMode は upsertKeep、upsertDelete、emptyOnly、replace のいずれかです: {table.Source}");
            if (table.OperationMode is null &&
                (table.CopyMode is not ("full" or "upsert") ||
                 table.InitialLoadMode is not ("requireEmpty" or "truncate") ||
                 table.DeleteMissing && table.CopyMode != "upsert"))
                throw new ConfigException($"旧形式の処理方式設定が不正です: {table.Source}");
            if (table.EffectiveDeleteMissing && !table.BackupBeforeCopy)
                throw new ConfigException($"削除ありの差分更新ではbackupBeforeCopy=trueが必要です: {table.Source}");
        }
    }

    internal static string ExpandEnvironmentVariables(string value) =>
        Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value)
            ?? throw new ConfigException($"環境変数 {match.Groups[1].Value} が設定されていません。"));
}

internal sealed class TableConfig
{
    public string Source { get; init; } = "";
    public string? Target { get; init; }
    public List<string>? Columns { get; init; }
    public string? OperationMode { get; init; }

    // 旧形式の設定ファイルとの互換用。新規設定ではoperationModeを使用する。
    public string InitialLoadMode { get; init; } = "requireEmpty";
    public string CopyMode { get; init; } = "full";
    public bool DeleteMissing { get; init; }
    public bool BackupBeforeCopy { get; init; } = true;
    public string EffectiveTarget => string.IsNullOrWhiteSpace(Target) ? Source : Target;
    public string EffectiveOperationMode => OperationMode ?? (CopyMode, DeleteMissing, InitialLoadMode) switch
    {
        ("upsert", true, _) => "upsertDelete",
        ("upsert", false, _) => "upsertKeep",
        (_, _, "truncate") => "replace",
        _ => "emptyOnly"
    };
    public string EffectiveCopyMode => EffectiveOperationMode is "upsertKeep" or "upsertDelete" ? "upsert" : "full";
    public bool EffectiveDeleteMissing => EffectiveOperationMode == "upsertDelete";
    public string EffectiveInitialLoadMode => EffectiveOperationMode == "replace" ? "truncate" : "requireEmpty";
}

internal sealed class ConfigException(string message) : Exception(message);
