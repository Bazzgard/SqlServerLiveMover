using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlServerLiveMover;

public sealed record BackupEntry(
    string BackupSchema,
    string BackupTable,
    string TargetSchema,
    string TargetTable,
    DateTime CreatedAtUtc,
    long RowCount,
    string Reason,
    bool IsInferred)
{
    public string BackupQualifiedName => $"{BackupSchema}.{BackupTable}";
    public string TargetQualifiedName => $"{TargetSchema}.{TargetTable}";
}

internal sealed class BackupService(int commandTimeout, Action<string>? logger = null)
{
    private const string Prefix = "__SqlMoverBackup_";
    private static readonly Regex BackupPattern = new(
        @"^__SqlMoverBackup_(?<target>.+)_(?<date>\d{8})_(?<time>\d{6})_(?<id>[0-9a-fA-F]{8})$",
        RegexOptions.Compiled);
    private readonly Action<string> log = logger ?? Console.WriteLine;
    private readonly MetadataReader metadata = new(commandTimeout);
    private readonly LocalBackupCatalogStore catalog = new();

    public async Task<IReadOnlyList<BackupEntry>> ListAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        var endpointId = LocalBackupCatalogStore.GetEndpointId(connection.DataSource, connection.Database);
        var localRecords = await catalog.GetAsync(endpointId, cancellationToken);
        const string sql = """
            SELECT s.name, t.name, t.create_date,
                   COALESCE(SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END), 0)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.partitions p ON p.object_id = t.object_id
            WHERE t.name LIKE N'[_][_]SqlMoverBackup[_]%'
            GROUP BY s.name, t.name, t.create_date
            ORDER BY t.create_date DESC;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeout };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<BackupEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var local = localRecords.FirstOrDefault(record =>
                record.BackupSchema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
                record.BackupTable.Equals(table, StringComparison.OrdinalIgnoreCase));
            var inferred = local is null;
            var targetSchema = local?.TargetSchema ?? schema;
            var targetTable = local?.TargetTable ?? InferTargetTable(table);
            entries.Add(new BackupEntry(
                schema, table, targetSchema, targetTable,
                local?.CreatedAtUtc ?? reader.GetDateTime(2),
                Convert.ToInt64(reader.GetValue(3)), local?.Reason ?? "legacy", inferred));
        }
        return entries;
    }

    public async Task DeleteAsync(
        string connectionString, BackupEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        var endpointId = LocalBackupCatalogStore.GetEndpointId(connection.DataSource, connection.Database);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var backup = new SqlName(entry.BackupSchema, entry.BackupTable);
            var sql = $"DROP TABLE {backup.Quoted};";
            await using var command = new SqlCommand(sql, connection, transaction)
            { CommandTimeout = commandTimeout };
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        try
        {
            await catalog.RemoveAsync(
                endpointId, entry.BackupSchema, entry.BackupTable, cancellationToken);
        }
        catch (Exception exception)
        {
            log($"警告: ローカル管理カタログを更新できませんでした: {exception.Message}");
        }
        log($"バックアップを削除しました: {entry.BackupQualifiedName}");
    }

    public async Task<long> RestoreAsync(
        string connectionString,
        BackupEntry entry,
        bool backupBeforeRestore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetTable))
            throw new InvalidOperationException("復元先テーブルを特定できないバックアップです。");

        await using var connection = await OpenAsync(connectionString, cancellationToken);
        var targetName = new SqlName(entry.TargetSchema, entry.TargetTable);
        var backupName = new SqlName(entry.BackupSchema, entry.BackupTable);
        var target = await metadata.ReadTableAsync(connection, targetName, cancellationToken);
        var backup = await metadata.ReadTableAsync(connection, backupName, cancellationToken);

        if (backupBeforeRestore)
            await CreateAsync(connection, target, "pre-restore", cancellationToken);

        var backupColumns = backup.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var restoreColumns = target.Columns
            .Where(column => !column.IsComputed && !column.IsRowVersion && column.GeneratedAlwaysType == 0)
            .ToList();
        foreach (var column in restoreColumns)
            if (!backupColumns.ContainsKey(column.Name))
                throw new InvalidOperationException($"バックアップに必要な列がありません: {column.Name}");

        var columns = string.Join(", ", restoreColumns.Select(column => SqlName.Quote(column.Name)));
        var identity = restoreColumns.Any(column => column.IsIdentity);
        var insertBody = $"""
            INSERT INTO {targetName.Quoted} ({columns})
            SELECT {columns} FROM {backupName.Quoted};
            SET @Restored = @@ROWCOUNT;
            """;
        var sql = $"""
            DECLARE @Restored bigint = 0;
            DELETE FROM {targetName.Quoted};
            {(identity ? $"SET IDENTITY_INSERT {targetName.Quoted} ON;" : "")}
            BEGIN TRY
                {insertBody}
                {(identity ? $"SET IDENTITY_INSERT {targetName.Quoted} OFF;" : "")}
            END TRY
            BEGIN CATCH
                {(identity ? $"SET IDENTITY_INSERT {targetName.Quoted} OFF;" : "")}
                THROW;
            END CATCH;
            SELECT @Restored;
            """;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = new SqlCommand(sql, connection, transaction)
            { CommandTimeout = commandTimeout };
            var restored = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            log($"復元しました: {entry.BackupQualifiedName} -> {entry.TargetQualifiedName} ({restored:N0}行)");
            return restored;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<BackupEntry?> CreateAsync(
        SqlConnection connection, TableInfo target, string reason, CancellationToken cancellationToken)
    {
        var count = await CountAsync(connection, target.Name, null, cancellationToken);
        if (count == 0)
        {
            log($"バックアップ省略: {target.Name.Canonical} は空です。");
            return null;
        }

        var suffix = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
        var backupName = BuildBackupName(target.Name, suffix);
        var columns = string.Join(", ", target.Columns.Select(column => column.IsRowVersion
            ? $"CONVERT(binary(8), {SqlName.Quote(column.Name)}) AS {SqlName.Quote(column.Name)}"
            : SqlName.Quote(column.Name)));
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var sql = $"""
                SELECT {columns} INTO {backupName.Quoted}
                FROM {target.Name.Quoted} WITH (HOLDLOCK, TABLOCK);
                """;
            await using var command = new SqlCommand(sql, connection, transaction)
            { CommandTimeout = commandTimeout };
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        var createdAtUtc = DateTime.UtcNow;
        var endpointId = LocalBackupCatalogStore.GetEndpointId(connection.DataSource, connection.Database);
        try
        {
            await catalog.AddRangeAsync(
                [new LocalBackupRecord(
                    endpointId, backupName.Schema, backupName.Table,
                    target.Name.Schema, target.Name.Table, createdAtUtc, count, reason)],
                cancellationToken);
        }
        catch (Exception exception)
        {
            log($"警告: ローカル管理カタログへ記録できませんでした: {exception.Message}");
        }
        var entry = new BackupEntry(
            backupName.Schema, backupName.Table, target.Name.Schema, target.Name.Table,
            createdAtUtc, count, reason, false);
        log($"バックアップ作成: {entry.BackupQualifiedName} ({count:N0}行)");
        return entry;
    }

    private static string InferTargetTable(string backupTable)
    {
        var match = BackupPattern.Match(backupTable);
        return match.Success ? match.Groups["target"].Value : "";
    }

    private static SqlName BuildBackupName(SqlName target, string suffix)
    {
        var maximumBaseLength = 128 - Prefix.Length - 1 - suffix.Length;
        var baseName = target.Table.Length <= maximumBaseLength
            ? target.Table
            : target.Table[..maximumBaseLength];
        return new SqlName(target.Schema, $"{Prefix}{baseName}_{suffix}");
    }

    private async Task<long> CountAsync(
        SqlConnection connection, SqlName table, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table.Quoted};", connection, transaction)
        { CommandTimeout = commandTimeout };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
