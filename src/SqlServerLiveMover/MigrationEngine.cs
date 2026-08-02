using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlServerLiveMover;

internal sealed class MigrationEngine
{
    private readonly AppConfig config;
    private readonly Action<string> log;

    public MigrationEngine(AppConfig config, Action<string>? log = null)
    {
        this.config = config;
        this.log = log ?? Console.WriteLine;
    }

    public async Task PreflightAsync(CancellationToken cancellationToken)
    {
        await using var source = await OpenAsync(config.SourceConnectionString, cancellationToken);
        await using var target = await OpenAsync(config.TargetConnectionString, cancellationToken);
        var (plans, isolation) = await new PlanBuilder(config).BuildAsync(source, target, cancellationToken);
        ValidateConsistency(isolation);

        log($"OK: {plans.Count}テーブルの接続と構成を確認しました。移行元への設定変更は行っていません。");
        foreach (var plan in plans)
        {
            var modifiesExistingRows = plan.Config.EffectiveOperationMode != "emptyOnly";
            var backupStatus = !modifiesExistingRows
                ? "対象外（空のみ）"
                : plan.Config.BackupBeforeCopy ? "有効" : "無効";
            var copyMode = plan.Config.EffectiveOperationMode switch
            {
                "upsertKeep" => "差分更新（削除なし）",
                "upsertDelete" => "差分更新（削除あり）",
                "replace" => "全削除してコピー",
                _ => "空テーブルのみ"
            };
            log($"  {plan.Source.Name.Canonical} -> {plan.Target.Name.Canonical} " +
                $"({plan.Columns.Count}列, PK: {(plan.Keys.Count == 0 ? "なし" : string.Join(", ", plan.Keys))}, " +
                $"方式: {copyMode}, 事前バックアップ: {backupStatus})");
        }
        PrintConsistencyDescription(isolation);
    }

    public async Task CopyAsync(CancellationToken cancellationToken)
    {
        await using var source = await OpenAsync(config.SourceConnectionString, cancellationToken);
        await using var target = await OpenAsync(config.TargetConnectionString, cancellationToken);
        var (plans, isolation) = await new PlanBuilder(config).BuildAsync(source, target, cancellationToken);
        ValidateConsistency(isolation);

        await using var sourceTransaction = (SqlTransaction)await source.BeginTransactionAsync(
            GetIsolationLevel(), cancellationToken);
        try
        {
            foreach (var plan in plans)
                await CopyTableAtomicallyAsync(
                    source, target, sourceTransaction, plan, cancellationToken);
            await sourceTransaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await sourceTransaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        log("コピーが完了しました。移行元のデータや設定は変更していません。");
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        await using var source = await OpenAsync(config.SourceConnectionString, cancellationToken);
        await using var target = await OpenAsync(config.TargetConnectionString, cancellationToken);
        var (plans, _) = await new PlanBuilder(config).BuildAsync(source, target, cancellationToken);
        var allEqual = true;
        foreach (var plan in plans)
        {
            var sourceCount = await CountAsync(source, plan.Source.Name, cancellationToken);
            var targetCount = await CountAsync(target, plan.Target.Name, cancellationToken);
            var equal = sourceCount == targetCount;
            allEqual &= equal;
            log($"{(equal ? "OK" : "NG")}: {plan.Source.Name.Canonical} " +
                $"source={sourceCount:N0}, target={targetCount:N0}");
        }
        if (!allEqual)
            throw new VerificationException(
                "件数が一致しないテーブルがあります。コピー後も移行元が更新されている場合、差分同期を行わないため件数差は発生します。");
    }

    private async Task CopyTableAtomicallyAsync(
        SqlConnection source,
        SqlConnection target,
        SqlTransaction sourceTransaction,
        TablePlan plan,
        CancellationToken cancellationToken)
    {
        await BackupTargetAsync(target, plan, cancellationToken);
        await using var targetTransaction = (SqlTransaction)await target.BeginTransactionAsync(cancellationToken);
        try
        {
            if (plan.Config.EffectiveCopyMode == "upsert")
            {
                var result = await UpsertRowsAsync(
                    source, target, sourceTransaction, targetTransaction, plan, cancellationToken);
                await targetTransaction.CommitAsync(cancellationToken);
                log($"差分更新完了: {plan.Source.Name.Canonical} " +
                    $"(追加 {result.Inserted:N0}行, 更新 {result.Updated:N0}行, 削除 {result.Deleted:N0}行)");
                return;
            }

            await PrepareTargetAsync(target, targetTransaction, plan, cancellationToken);
            var copied = await CopyRowsAsync(
                source, target, sourceTransaction, targetTransaction, plan, cancellationToken);
            await targetTransaction.CommitAsync(cancellationToken);
            log($"コピー完了: {plan.Source.Name.Canonical} ({copied:N0}行)");
        }
        catch
        {
            await targetTransaction.RollbackAsync(CancellationToken.None);
            log($"コピー失敗: {plan.Source.Name.Canonical}。このテーブルへの変更はロールバックしました。");
            throw;
        }
    }

    private async Task BackupTargetAsync(
        SqlConnection target,
        TablePlan plan,
        CancellationToken cancellationToken)
    {
        var modifiesExistingRows = plan.Config.EffectiveOperationMode != "emptyOnly";
        if (!plan.Config.BackupBeforeCopy || !modifiesExistingRows) return;
        var service = new BackupService(config.CommandTimeoutSeconds, log);
        await service.CreateAsync(target, plan.Target, "copy", cancellationToken);
    }

    private async Task PrepareTargetAsync(
        SqlConnection target,
        SqlTransaction transaction,
        TablePlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Config.EffectiveInitialLoadMode == "truncate")
        {
            await ExecuteAsync(target, transaction, $"TRUNCATE TABLE {plan.Target.Name.Quoted};", cancellationToken);
            return;
        }

        var count = await CountAsync(target, plan.Target.Name, cancellationToken, transaction);
        if (count != 0)
            throw new InvalidOperationException(
                $"移行先が空ではありません: {plan.Target.Name.Canonical} ({count:N0}行)。" +
                "空にするか、明示的に initialLoadMode を truncate にしてください。");
    }

    private async Task<long> CopyRowsAsync(
        SqlConnection source,
        SqlConnection target,
        SqlTransaction sourceTransaction,
        SqlTransaction targetTransaction,
        TablePlan plan,
        CancellationToken cancellationToken)
    {
        var columns = ColumnList(plan.Columns);
        await using var command = new SqlCommand(
            $"SELECT {columns} FROM {plan.Source.Name.Quoted};", source, sourceTransaction)
        { CommandTimeout = config.CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        using var bulk = new SqlBulkCopy(
            target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints, targetTransaction)
        {
            DestinationTableName = plan.Target.Name.Quoted,
            BatchSize = config.BatchSize,
            BulkCopyTimeout = config.CommandTimeoutSeconds,
            EnableStreaming = true,
            NotifyAfter = config.BatchSize
        };
        foreach (var column in plan.Columns)
            bulk.ColumnMappings.Add(column.Name, column.Name);
        bulk.SqlRowsCopied += (_, args) =>
            log($"  コピー中: {plan.Source.Name.Canonical} {args.RowsCopied:N0}行");
        await bulk.WriteToServerAsync(reader, cancellationToken);
        return await CountAsync(target, plan.Target.Name, cancellationToken, targetTransaction);
    }

    private async Task<DifferenceResult> UpsertRowsAsync(
        SqlConnection source,
        SqlConnection target,
        SqlTransaction sourceTransaction,
        SqlTransaction targetTransaction,
        TablePlan plan,
        CancellationToken cancellationToken)
    {
        const string stage = "#SqlMoverStage";
        var columns = ColumnList(plan.Columns);
        var createStageSql = $"""
            DROP TABLE IF EXISTS {stage};
            SELECT TOP (0) {columns}
            INTO {stage}
            FROM {plan.Target.Name.Quoted};
            """;
        await ExecuteAsync(target, targetTransaction, createStageSql, cancellationToken);

        await using (var sourceCommand = new SqlCommand(
                         $"SELECT {columns} FROM {plan.Source.Name.Quoted};", source, sourceTransaction)
                     { CommandTimeout = config.CommandTimeoutSeconds })
        await using (var reader = await sourceCommand.ExecuteReaderAsync(
                         CommandBehavior.SequentialAccess, cancellationToken))
        using (var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity, targetTransaction)
               {
                   DestinationTableName = stage,
                   BatchSize = config.BatchSize,
                   BulkCopyTimeout = config.CommandTimeoutSeconds,
                   EnableStreaming = true,
                   NotifyAfter = config.BatchSize
               })
        {
            foreach (var column in plan.Columns)
                bulk.ColumnMappings.Add(column.Name, column.Name);
            bulk.SqlRowsCopied += (_, args) =>
                log($"  比較データ読込中: {plan.Source.Name.Canonical} {args.RowsCopied:N0}行");
            await bulk.WriteToServerAsync(reader, cancellationToken);
        }

        var keySet = plan.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var join = string.Join(" AND ", plan.Keys.Select(key =>
            $"T.{SqlName.Quote(key)} = S.{SqlName.Quote(key)}"));
        var updateColumns = plan.Columns
            .Where(column => !keySet.Contains(column.Name) && !column.IsIdentity)
            .ToList();

        var updated = 0;
        if (updateColumns.Count > 0)
        {
            var assignments = string.Join(", ", updateColumns.Select(column =>
                $"T.{SqlName.Quote(column.Name)} = S.{SqlName.Quote(column.Name)}"));
            var sourceValues = string.Join(", ", updateColumns.Select(column =>
                $"S.{SqlName.Quote(column.Name)}"));
            var targetValues = string.Join(", ", updateColumns.Select(column =>
                $"T.{SqlName.Quote(column.Name)}"));
            var updateSql = $"""
                UPDATE T
                SET {assignments}
                FROM {plan.Target.Name.Quoted} AS T
                INNER JOIN {stage} AS S ON {join}
                WHERE EXISTS
                (
                    SELECT {sourceValues}
                    EXCEPT
                    SELECT {targetValues}
                );
                """;
            updated = await ExecuteNonQueryAsync(target, targetTransaction, updateSql, cancellationToken);
        }

        var identity = plan.Columns.Any(column => column.IsIdentity);
        var insertBody = $"""
            INSERT INTO {plan.Target.Name.Quoted} ({columns})
            SELECT {columns}
            FROM {stage} AS S
            WHERE NOT EXISTS
            (
                SELECT 1 FROM {plan.Target.Name.Quoted} AS T WHERE {join}
            );
            SET @Inserted = @@ROWCOUNT;
            """;
        var insertSql = identity
            ? $"""
                DECLARE @Inserted int = 0;
                SET IDENTITY_INSERT {plan.Target.Name.Quoted} ON;
                BEGIN TRY
                    {insertBody}
                    SET IDENTITY_INSERT {plan.Target.Name.Quoted} OFF;
                END TRY
                BEGIN CATCH
                    SET IDENTITY_INSERT {plan.Target.Name.Quoted} OFF;
                    THROW;
                END CATCH;
                SELECT @Inserted;
                """
            : $"""
                DECLARE @Inserted int = 0;
                {insertBody}
                SELECT @Inserted;
                """;
        var inserted = await ExecuteScalarIntAsync(target, targetTransaction, insertSql, cancellationToken);

        var deleted = 0;
        if (plan.Config.EffectiveDeleteMissing)
        {
            var deleteSql = $"""
                DELETE T
                FROM {plan.Target.Name.Quoted} AS T
                WHERE NOT EXISTS
                (
                    SELECT 1 FROM {stage} AS S WHERE {join}
                );
                """;
            deleted = await ExecuteNonQueryAsync(target, targetTransaction, deleteSql, cancellationToken);
        }

        await ExecuteAsync(target, targetTransaction, $"DROP TABLE {stage};", cancellationToken);
        return new DifferenceResult(inserted, updated, deleted);
    }

    private void ValidateConsistency(DatabaseIsolationOptions options)
    {
        if (config.SourceConsistency == "snapshot" && !options.SnapshotIsolationEnabled)
            throw new InvalidOperationException(
                "sourceConsistency=snapshotですが、移行元DBでALLOW_SNAPSHOT_ISOLATIONが有効ではありません。" +
                "元DBを変更しない場合はlockedを選べますが、コピー中の書き込みが待機します。" +
                "整合性を保証しないreadCommittedも選択できます。");
    }

    private void PrintConsistencyDescription(DatabaseIsolationOptions options)
    {
        var message = config.SourceConsistency switch
        {
            "snapshot" => "snapshot: コピー中の書き込みを妨げず、全対象テーブルを同じ時点で読み取ります。",
            "locked" => "locked: 元DBの設定変更なしで整合性を保ちますが、コピー中の書き込みが待機する場合があります。",
            _ => "readCommitted: 元DBへの影響は小さい一方、コピー中の変更を含むため一点時点の整合性は保証しません。"
        };
        log($"整合性モード: {message}");
        log($"DB設定: ALLOW_SNAPSHOT_ISOLATION={(options.SnapshotIsolationEnabled ? "ON" : "OFF")}, " +
            $"READ_COMMITTED_SNAPSHOT={(options.ReadCommittedSnapshotEnabled ? "ON" : "OFF")}");
    }

    private IsolationLevel GetIsolationLevel() => config.SourceConsistency switch
    {
        "snapshot" => IsolationLevel.Snapshot,
        "locked" => IsolationLevel.Serializable,
        _ => IsolationLevel.ReadCommitted
    };

    private async Task<long> CountAsync(
        SqlConnection connection,
        SqlName table,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null)
    {
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table.Quoted};", connection, transaction)
        { CommandTimeout = config.CommandTimeoutSeconds };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction)
        { CommandTimeout = config.CommandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> ExecuteNonQueryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction)
        { CommandTimeout = config.CommandTimeoutSeconds };
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> ExecuteScalarIntAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction)
        { CommandTimeout = config.CommandTimeoutSeconds };
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string ColumnList(IEnumerable<ColumnInfo> columns) =>
        string.Join(", ", columns.Select(c => SqlName.Quote(c.Name)));

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class VerificationException(string message) : Exception(message);

internal sealed record DifferenceResult(int Inserted, int Updated, int Deleted);
