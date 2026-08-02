using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlServerLiveMover;

internal sealed record LocalBackupRecord(
    string EndpointId,
    string BackupSchema,
    string BackupTable,
    string TargetSchema,
    string TargetTable,
    DateTime CreatedAtUtc,
    long RowCount,
    string Reason);

internal sealed class LocalBackupCatalogDocument
{
    public List<LocalBackupRecord> Backups { get; init; } = [];
}

internal sealed class LocalBackupCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string CatalogPath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "logs",
        "backup-catalog.json");

    public static string GetEndpointId(string dataSource, string database)
    {
        var value = $"{dataSource}|{database}".ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public async Task<IReadOnlyList<LocalBackupRecord>> GetAsync(
        string endpointId, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return Load().Backups
                .Where(record => record.EndpointId == endpointId)
                .ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task AddRangeAsync(
        IEnumerable<LocalBackupRecord> records, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var document = Load();
            foreach (var record in records)
            {
                document.Backups.RemoveAll(existing =>
                    existing.EndpointId == record.EndpointId &&
                    existing.BackupSchema.Equals(record.BackupSchema, StringComparison.OrdinalIgnoreCase) &&
                    existing.BackupTable.Equals(record.BackupTable, StringComparison.OrdinalIgnoreCase));
                document.Backups.Add(record);
            }
            Save(document);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RemoveAsync(
        string endpointId, string schema, string table, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var document = Load();
            document.Backups.RemoveAll(record =>
                record.EndpointId == endpointId &&
                record.BackupSchema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
                record.BackupTable.Equals(table, StringComparison.OrdinalIgnoreCase));
            Save(document);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static LocalBackupCatalogDocument Load()
    {
        if (!File.Exists(CatalogPath)) return new LocalBackupCatalogDocument();
        return JsonSerializer.Deserialize<LocalBackupCatalogDocument>(File.ReadAllText(CatalogPath), JsonOptions)
               ?? new LocalBackupCatalogDocument();
    }

    private static void Save(LocalBackupCatalogDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        var temporary = CatalogPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, CatalogPath, true);
    }
}
