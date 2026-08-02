namespace SqlServerLiveMover;

internal sealed record ColumnInfo(
    string Name,
    string TypeName,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool IsRowVersion,
    bool HasDefault,
    int GeneratedAlwaysType);

internal sealed record TableInfo(
    SqlName Name,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> PrimaryKey);

internal sealed record TablePlan(
    TableConfig Config,
    TableInfo Source,
    TableInfo Target,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> Keys)
{
}

internal sealed record DatabaseIsolationOptions(bool SnapshotIsolationEnabled, bool ReadCommittedSnapshotEnabled);
