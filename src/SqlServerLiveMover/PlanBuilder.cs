using Microsoft.Data.SqlClient;

namespace SqlServerLiveMover;

internal sealed class PlanBuilder(AppConfig config)
{
    private readonly MetadataReader _metadata = new(config.CommandTimeoutSeconds);

    public async Task<(IReadOnlyList<TablePlan> Plans, DatabaseIsolationOptions Isolation)> BuildAsync(
        SqlConnection source, SqlConnection target, CancellationToken cancellationToken)
    {
        var plans = new List<TablePlan>();
        foreach (var item in config.Tables)
        {
            var sourceName = SqlName.Parse(item.Source);
            var targetName = SqlName.Parse(item.EffectiveTarget);
            if (source.DataSource.Equals(target.DataSource, StringComparison.OrdinalIgnoreCase) &&
                source.Database.Equals(target.Database, StringComparison.OrdinalIgnoreCase) &&
                sourceName.Canonical.Equals(targetName.Canonical, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"移行元と移行先が同じテーブルです: {sourceName.Canonical}");
            var sourceInfo = await _metadata.ReadTableAsync(source, sourceName, cancellationToken);
            var targetInfo = await _metadata.ReadTableAsync(target, targetName, cancellationToken);
            plans.Add(BuildPlan(item, sourceInfo, targetInfo));
        }
        return (plans, await _metadata.ReadIsolationOptionsAsync(source, cancellationToken));
    }

    private static TablePlan BuildPlan(TableConfig config, TableInfo source, TableInfo target)
    {
        if (config.EffectiveCopyMode == "upsert" && source.PrimaryKey.Count == 0)
            throw new InvalidOperationException($"差分更新には主キーが必要です: {source.Name.Canonical}");

        var targetByName = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var sourceWritable = source.Columns
            .Where(c => !c.IsComputed && !c.IsRowVersion && c.GeneratedAlwaysType == 0)
            .ToList();
        var selected = config.Columns is null
            ? sourceWritable
            : config.Columns.Select(name => sourceWritable.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"移行元に書き込み可能な列がありません: {source.Name.Canonical}.{name}"))
                .ToList();

        foreach (var key in source.PrimaryKey)
            if (!selected.Any(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"columnsには主キー列を含めてください: {source.Name.Canonical}.{key}");

        if (config.EffectiveCopyMode == "upsert")
        {
            var unsupportedComparisonTypes = new HashSet<string>(
                ["text", "ntext", "image", "xml", "geometry", "geography", "hierarchyid"],
                StringComparer.OrdinalIgnoreCase);
            var unsupported = selected.FirstOrDefault(column =>
                !source.PrimaryKey.Contains(column.Name, StringComparer.OrdinalIgnoreCase) &&
                unsupportedComparisonTypes.Contains(column.TypeName));
            if (unsupported is not null)
                throw new InvalidOperationException(
                    $"差分比較に対応していない列型です: {source.Name.Canonical}.{unsupported.Name} ({unsupported.TypeName})。" +
                    "この列をcolumnsから除外するか、全件コピーを選択してください。");
        }

        foreach (var column in selected)
        {
            if (!targetByName.TryGetValue(column.Name, out var targetColumn))
                throw new InvalidOperationException($"移行先に列がありません: {target.Name.Canonical}.{column.Name}");
            if (targetColumn.IsComputed || targetColumn.IsRowVersion || targetColumn.GeneratedAlwaysType != 0)
                throw new InvalidOperationException($"移行先列は書き込みできません: {target.Name.Canonical}.{column.Name}");
            if (!column.Name.Equals(targetColumn.Name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"列名の大文字・小文字が一致しません: {source.Name.Canonical}.{column.Name}, " +
                    $"{target.Name.Canonical}.{targetColumn.Name}");
            if (!SameSqlType(column, targetColumn))
                throw new InvalidOperationException(
                    $"列の型・サイズが一致しません: {source.Name.Canonical}.{column.Name}=" +
                    $"{DescribeType(column)}, {target.Name.Canonical}.{column.Name}={DescribeType(targetColumn)}");
        }

        foreach (var required in target.Columns.Where(c =>
                     !selected.Any(s => s.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) &&
                     !c.IsNullable && !c.IsIdentity && !c.IsComputed && !c.IsRowVersion &&
                     !c.HasDefault && c.GeneratedAlwaysType == 0))
            throw new InvalidOperationException($"移行先の必須列がcolumnsに含まれていません: {target.Name.Canonical}.{required.Name}");

        if (!source.PrimaryKey.SequenceEqual(target.PrimaryKey, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"移行先の主キーが一致しません: source=({string.Join(", ", source.PrimaryKey)}), " +
                $"target=({string.Join(", ", target.PrimaryKey)})");

        return new TablePlan(config, source, target, selected, source.PrimaryKey);
    }

    private static bool SameSqlType(ColumnInfo source, ColumnInfo target)
    {
        if (!source.TypeName.Equals(target.TypeName, StringComparison.OrdinalIgnoreCase)) return false;
        return source.TypeName.ToLowerInvariant() switch
        {
            "binary" or "varbinary" or "char" or "varchar" or "nchar" or "nvarchar" =>
                source.MaxLength == target.MaxLength,
            "decimal" or "numeric" => source.Precision == target.Precision && source.Scale == target.Scale,
            "datetime2" or "datetimeoffset" or "time" => source.Scale == target.Scale,
            _ => true
        };
    }

    private static string DescribeType(ColumnInfo column) => column.TypeName.ToLowerInvariant() switch
    {
        "binary" or "varbinary" or "char" or "varchar" =>
            $"{column.TypeName}({(column.MaxLength == -1 ? "max" : column.MaxLength)})",
        "nchar" or "nvarchar" =>
            $"{column.TypeName}({(column.MaxLength == -1 ? "max" : column.MaxLength / 2)})",
        "decimal" or "numeric" => $"{column.TypeName}({column.Precision},{column.Scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{column.TypeName}({column.Scale})",
        _ => column.TypeName
    };
}
