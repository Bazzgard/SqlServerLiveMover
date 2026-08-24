using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SqlServerLiveMover.Gui;

internal sealed class ConfigDocument
{
    public string SourceConnectionString { get; set; } = "${SQL_MOVER_SOURCE}";
    public string TargetConnectionString { get; set; } = "${SQL_MOVER_TARGET}";
    public int BatchSize { get; set; } = 5_000;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public string SourceConsistency { get; set; } = "snapshot";
    public ObservableCollection<TableDocument> Tables { get; set; } = [];

    [JsonIgnore]
    public bool BackupBeforeCopy { get; set; } = true;

    public void NormalizeAfterLoad()
    {
        foreach (var table in Tables) table.NormalizeOperationMode();
        BackupBeforeCopy = Tables.Count == 0 || Tables.All(table => table.BackupBeforeCopy);
    }

    public void ApplyGlobalSettings()
    {
        foreach (var table in Tables)
        {
            table.NormalizeOperationMode();
            table.BackupBeforeCopy = BackupBeforeCopy;
        }
    }
}

internal sealed class TableDocument
{
    [JsonIgnore]
    public bool IsSelectedForCopy { get; set; } = true;

    public string Source { get; set; } = "dbo.SourceTable";
    public string? Target { get; set; }

    [JsonPropertyName("columns")]
    public List<string>? ColumnList { get; set; }

    public string? OperationMode { get; set; }

    // 旧形式の設定を読み込むためだけに保持し、NormalizeOperationMode後は保存しない。
    public string? InitialLoadMode { get; set; }
    public string? CopyMode { get; set; }
    public bool? DeleteMissing { get; set; }
    public bool BackupBeforeCopy { get; set; } = true;

    public void NormalizeOperationMode()
    {
        OperationMode ??= (CopyMode, DeleteMissing, InitialLoadMode) switch
        {
            ("upsert", true, _) => "upsertDelete",
            ("upsert", _, _) => "upsertKeep",
            (_, _, "truncate") => "replace",
            _ => "emptyOnly"
        };
        InitialLoadMode = null;
        CopyMode = null;
        DeleteMissing = null;
    }

    [JsonIgnore]
    public string ColumnsText
    {
        get => ColumnList is null ? "" : string.Join(", ", ColumnList);
        set => ColumnList = string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
