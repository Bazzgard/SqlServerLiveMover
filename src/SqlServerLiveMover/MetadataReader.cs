using Microsoft.Data.SqlClient;

namespace SqlServerLiveMover;

internal sealed class MetadataReader(int commandTimeout)
{
    public async Task<TableInfo> ReadTableAsync(SqlConnection connection, SqlName table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable,
                   c.is_identity, c.is_computed,
                   CASE WHEN ty.name IN ('timestamp', 'rowversion') THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
                   CASE WHEN c.default_object_id <> 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
                   c.generated_always_type
            FROM sys.columns c
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            WHERE c.object_id = OBJECT_ID(@qualifiedName, 'U')
            ORDER BY c.column_id;

            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@qualifiedName, 'U') AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal;

            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeout };
        command.Parameters.AddWithValue("@qualifiedName", table.Canonical);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<ColumnInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnInfo(
                reader.GetString(0), reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4),
                reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8),
                reader.GetBoolean(9), Convert.ToInt32(reader.GetValue(10))));
        }
        if (columns.Count == 0)
            throw new InvalidOperationException($"テーブルが見つかりません: {table.Canonical}");

        await reader.NextResultAsync(cancellationToken);
        var keys = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(reader.GetString(0));

        return new TableInfo(table, columns, keys);
    }

    public async Task<DatabaseIsolationOptions> ReadIsolationOptionsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT snapshot_isolation_state, is_read_committed_snapshot_on
            FROM sys.databases WHERE database_id = DB_ID();
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeout };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("移行元データベースの分離設定を取得できません。");
        return new DatabaseIsolationOptions(Convert.ToInt32(reader.GetValue(0)) == 1, reader.GetBoolean(1));
    }
}
