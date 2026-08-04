using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Data.SqlClient;

namespace SqlServerLiveMover.Gui;

public partial class CompareMigrationWindow : Window
{
    private const int PreviewRowLimit = 1_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConfigDocument document;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? previewCancellation;
    private readonly HashSet<string> currentPrimaryKeys = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AlignedPreviewRow> currentAlignedRows = [];
    private bool isBusy;
    private bool isSynchronizingScroll;
    private bool isSynchronizingSelection;

    public ObservableCollection<MigrationSelectionItem> Items { get; } = [];

    internal CompareMigrationWindow(ConfigDocument document)
    {
        InitializeComponent();
        this.document = document;
        foreach (var table in document.Tables)
            Items.Add(new MigrationSelectionItem(table));
        DataContext = this;
        EndpointText.Text = DescribeEndpoints();
        UpdateSelectionSummary();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Items.Count == 0)
        {
            StatusText.Text = "対象テーブルがありません。メイン画面で追加してください。";
            MoveButton.IsEnabled = false;
            PreflightButton.IsEnabled = false;
            return;
        }

        TableList.SelectedIndex = 0;
        await RefreshCurrentPreviewAsync();
    }

    private async void TableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || isBusy) return;
        UpdateSelectionSummary();
        await RefreshCurrentPreviewAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshCurrentPreviewAsync();

    private async Task RefreshCurrentPreviewAsync()
    {
        if (TableList.SelectedItem is not MigrationSelectionItem selected) return;

        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        var token = previewCancellation.Token;

        CurrentTableText.Text = $"{selected.Source}  →  {selected.TargetDisplay}";
        SourceCountText.Text = "取得中…";
        TargetCountText.Text = "取得中…";
        SchemaSummaryText.Text = "列情報を取得中…";
        DifferenceText.Text = "比較中";
        DifferenceBadge.Background = new SolidColorBrush(Color.FromRgb(255, 243, 205));
        SourcePreviewGrid.ItemsSource = null;
        TargetPreviewGrid.ItemsSource = null;
        SetPreviewBusy(true, "プレビューを読み込んでいます…");

        try
        {
            var result = await LoadPreviewAsync(selected.Document, token);
            token.ThrowIfCancellationRequested();
            currentPrimaryKeys.Clear();
            currentPrimaryKeys.UnionWith(result.PrimaryKeys);
            currentAlignedRows = result.AlignedRows;
            SourcePreviewGrid.ItemsSource = result.Source.Rows.DefaultView;
            TargetPreviewGrid.ItemsSource = result.Target.Rows.DefaultView;
            SourcePreviewGrid.SelectedItems.Clear();
            TargetPreviewGrid.SelectedItems.Clear();
            UpdateRowSelectionSummary();
            SourceCountText.Text = $"{result.Source.Count:N0} 件";
            TargetCountText.Text = $"{result.Target.Count:N0} 件";
            var keys = result.PrimaryKeys.Count == 0
                ? "主キーなし（行順で表示）"
                : $"主キー: {string.Join(", ", result.PrimaryKeys)}（水色）";
            SchemaSummaryText.Text = $"比較列 {result.ColumnCount}列 / {keys}";

            var difference = result.Target.Count - result.Source.Count;
            if (difference == 0)
            {
                DifferenceText.Text = "件数一致";
                DifferenceText.Foreground = new SolidColorBrush(Color.FromRgb(23, 102, 67));
                DifferenceBadge.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231));
            }
            else
            {
                DifferenceText.Text = $"差 {difference:+#,0;-#,0} 件";
                DifferenceText.Foreground = new SolidColorBrush(Color.FromRgb(154, 52, 18));
                DifferenceBadge.Background = new SolidColorBrush(Color.FromRgb(255, 237, 213));
            }
            StatusText.Text = $"{selected.Source} のプレビューを更新しました";
        }
        catch (OperationCanceledException)
        {
            // 別のテーブルを選択した場合は、新しい読み込みへ引き継ぐ。
        }
        catch (Exception exception)
        {
            SourceCountText.Text = "取得失敗";
            TargetCountText.Text = "取得失敗";
            SchemaSummaryText.Text = exception.Message;
            DifferenceText.Text = "未比較";
            StatusText.Text = $"プレビューを取得できません: {exception.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested) SetPreviewBusy(false, StatusText.Text);
        }
    }

    private async Task<TablePreviewPair> LoadPreviewAsync(TableDocument table, CancellationToken cancellationToken)
    {
        var sourceConnectionString = AppConfig.ExpandEnvironmentVariables(document.SourceConnectionString);
        var targetConnectionString = AppConfig.ExpandEnvironmentVariables(document.TargetConnectionString);
        await using var sourceConnection = new SqlConnection(sourceConnectionString);
        await using var targetConnection = new SqlConnection(targetConnectionString);
        await Task.WhenAll(
            sourceConnection.OpenAsync(cancellationToken),
            targetConnection.OpenAsync(cancellationToken));

        var metadataReader = new MetadataReader(document.CommandTimeoutSeconds);
        var sourceName = SqlName.Parse(table.Source);
        var targetName = SqlName.Parse(string.IsNullOrWhiteSpace(table.Target) ? table.Source : table.Target);
        var metadataTasks = new[]
        {
            metadataReader.ReadTableAsync(sourceConnection, sourceName, cancellationToken),
            metadataReader.ReadTableAsync(targetConnection, targetName, cancellationToken)
        };
        var metadata = await Task.WhenAll(metadataTasks);
        var sourceInfo = metadata[0];
        var targetInfo = metadata[1];

        var requestedColumns = table.ColumnList ?? sourceInfo.Columns
            .Where(column => !column.IsComputed && !column.IsRowVersion && column.GeneratedAlwaysType == 0)
            .Select(column => column.Name)
            .ToList();
        var targetColumns = targetInfo.Columns.Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchingPrimaryKeys = sourceInfo.PrimaryKey.Count > 0 &&
                                  sourceInfo.PrimaryKey.SequenceEqual(
                                      targetInfo.PrimaryKey, StringComparer.OrdinalIgnoreCase)
            ? sourceInfo.PrimaryKey
            : [];
        var previewColumns = matchingPrimaryKeys
            .Concat(requestedColumns)
            .Where(targetColumns.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (previewColumns.Count == 0)
            throw new InvalidOperationException("左右で比較できる列がありません。");

        var sourceTask = ReadTablePreviewAsync(
            sourceConnection, sourceName, previewColumns, matchingPrimaryKeys, cancellationToken);
        var targetTask = ReadTablePreviewAsync(
            targetConnection, targetName, previewColumns, matchingPrimaryKeys, cancellationToken);
        var previews = await Task.WhenAll(sourceTask, targetTask);
        var aligned = AlignPreviewRows(previews[0], previews[1], previewColumns, matchingPrimaryKeys.Count > 0);
        return new TablePreviewPair(
            aligned.Source,
            aligned.Target,
            previewColumns.Count,
            matchingPrimaryKeys,
            aligned.Rows);
    }

    private async Task<RawTablePreview> ReadTablePreviewAsync(
        SqlConnection connection,
        SqlName table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKeys,
        CancellationToken cancellationToken)
    {
        var countSql = $"SELECT COUNT_BIG(*) FROM {table.Quoted};";
        await using var countCommand = new SqlCommand(countSql, connection)
        {
            CommandTimeout = document.CommandTimeoutSeconds
        };
        var count = Convert.ToInt64(
            await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var projection = string.Join(", ", columns.Select(SqlName.Quote));
        var orderBy = primaryKeys.Count == 0
            ? ""
            : " ORDER BY " + string.Join(", ", primaryKeys.Select(key => $"{SqlName.Quote(key)} ASC"));
        var previewSql = $"SELECT TOP ({PreviewRowLimit}) {projection} FROM {table.Quoted}{orderBy};";
        await using var previewCommand = new SqlCommand(previewSql, connection)
        {
            CommandTimeout = document.CommandTimeoutSeconds
        };
        await using var reader = await previewCommand.ExecuteReaderAsync(cancellationToken);
        var keyIndexes = primaryKeys
            .Select(key => columns.ToList().FindIndex(column => column.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var rows = new List<PreviewRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var cells = new string[columns.Count];
            for (var index = 0; index < columns.Count; index++)
                cells[index] = FormatCellValue(reader.GetValue(index));
            var keyValues = keyIndexes.Select(index => reader.GetValue(index)).ToArray();
            var alignmentKey = keyIndexes.Length == 0
                ? rows.Count.ToString("D10", CultureInfo.InvariantCulture)
                : CreateAlignmentKey(keyValues);
            rows.Add(new PreviewRow(alignmentKey, cells, keyValues));
        }
        return new RawTablePreview(count, rows);
    }

    private static (TablePreview Source, TablePreview Target, IReadOnlyList<AlignedPreviewRow> Rows) AlignPreviewRows(
        RawTablePreview source,
        RawTablePreview target,
        IReadOnlyList<string> columns,
        bool alignByPrimaryKey)
    {
        var sourceRows = source.Rows.ToDictionary(row => row.AlignmentKey, StringComparer.Ordinal);
        var targetRows = target.Rows.ToDictionary(row => row.AlignmentKey, StringComparer.Ordinal);
        List<string> alignmentKeys;
        if (alignByPrimaryKey)
        {
            alignmentKeys = source.Rows.Select(row => row.AlignmentKey)
                .Concat(target.Rows.Select(row => row.AlignmentKey))
                .Distinct(StringComparer.Ordinal)
                .Select(key => sourceRows.GetValueOrDefault(key) ?? targetRows[key])
                .Order(PreviewRowAscendingComparer.Instance)
                .Select(row => row.AlignmentKey)
                .ToList();
        }
        else
        {
            var rowCount = Math.Max(source.Rows.Count, target.Rows.Count);
            alignmentKeys = Enumerable.Range(0, rowCount)
                .Select(index => index.ToString("D10", CultureInfo.InvariantCulture))
                .ToList();
        }

        var sourceTable = CreatePreviewTable(columns);
        var targetTable = CreatePreviewTable(columns);
        var alignedRows = new List<AlignedPreviewRow>(alignmentKeys.Count);
        foreach (var key in alignmentKeys)
        {
            var sourceRow = sourceRows.GetValueOrDefault(key);
            var targetRow = targetRows.GetValueOrDefault(key);
            AddPreviewRow(sourceTable, sourceRow);
            AddPreviewRow(targetTable, targetRow);
            alignedRows.Add(new AlignedPreviewRow(sourceRow, targetRow));
        }
        return (
            new TablePreview(source.Count, sourceTable),
            new TablePreview(target.Count, targetTable),
            alignedRows);
    }

    private static DataTable CreatePreviewTable(IReadOnlyList<string> columns)
    {
        var table = new DataTable { CaseSensitive = true };
        foreach (var column in columns) table.Columns.Add(column, typeof(string));
        return table;
    }

    private static void AddPreviewRow(DataTable table, PreviewRow? previewRow)
    {
        var row = table.NewRow();
        if (previewRow is not null)
            for (var index = 0; index < previewRow.Cells.Length; index++)
                row[index] = previewRow.Cells[index];
        else
            for (var index = 0; index < table.Columns.Count; index++)
                row[index] = "";
        table.Rows.Add(row);
    }

    private static string CreateAlignmentKey(IEnumerable<object> values) => string.Join(
        "|",
        values.Select(value =>
        {
            var text = FormatCellValue(value);
            return $"{text.Length}:{text}";
        }));

    private void PreviewGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (!currentPrimaryKeys.Contains(e.PropertyName)) return;

        e.Column.Header = $"🔑 {e.PropertyName}";
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(224, 247, 250))));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(21, 37, 54))));
        cellStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 10, 0)));
        cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var selectedTrigger = new Trigger
        {
            Property = DataGridCell.IsSelectedProperty,
            Value = true
        };
        selectedTrigger.Setters.Add(
            new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(128, 222, 234))));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(15, 55, 65))));
        cellStyle.Triggers.Add(selectedTrigger);
        e.Column.CellStyle = cellStyle;

        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(178, 235, 242))));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        e.Column.HeaderStyle = headerStyle;
    }

    private void PreviewGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (isSynchronizingScroll || e.OriginalSource is not ScrollViewer sourceViewer) return;
        var sourceGrid = (DataGrid)sender;
        var mainViewer = FindVisualChild<ScrollViewer>(sourceGrid);
        if (!ReferenceEquals(sourceViewer, mainViewer)) return;

        var targetGrid = ReferenceEquals(sourceGrid, SourcePreviewGrid)
            ? TargetPreviewGrid
            : SourcePreviewGrid;
        var targetViewer = FindVisualChild<ScrollViewer>(targetGrid);
        if (targetViewer is null) return;

        isSynchronizingScroll = true;
        try
        {
            if (e.VerticalChange != 0) targetViewer.ScrollToVerticalOffset(sourceViewer.VerticalOffset);
            if (e.HorizontalChange != 0) targetViewer.ScrollToHorizontalOffset(sourceViewer.HorizontalOffset);
        }
        finally
        {
            isSynchronizingScroll = false;
        }
    }

    private void PreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isSynchronizingSelection) return;
        var sourceGrid = (DataGrid)sender;
        var targetGrid = ReferenceEquals(sourceGrid, SourcePreviewGrid)
            ? TargetPreviewGrid
            : SourcePreviewGrid;
        var selectedIndexes = sourceGrid.SelectedItems.Cast<object>()
            .Select(item => sourceGrid.Items.IndexOf(item))
            .Where(index => index >= 0)
            .ToList();

        isSynchronizingSelection = true;
        try
        {
            targetGrid.SelectedItems.Clear();
            foreach (var index in selectedIndexes.Where(index => index < targetGrid.Items.Count))
                targetGrid.SelectedItems.Add(targetGrid.Items[index]);
        }
        finally
        {
            isSynchronizingSelection = false;
        }
        UpdateRowSelectionSummary();
    }

    private void SelectVisibleRows_Click(object sender, RoutedEventArgs e)
    {
        isSynchronizingSelection = true;
        try
        {
            SourcePreviewGrid.SelectedItems.Clear();
            TargetPreviewGrid.SelectedItems.Clear();
            for (var index = 0; index < currentAlignedRows.Count; index++)
            {
                if (currentAlignedRows[index].Source is null) continue;
                SourcePreviewGrid.SelectedItems.Add(SourcePreviewGrid.Items[index]);
                TargetPreviewGrid.SelectedItems.Add(TargetPreviewGrid.Items[index]);
            }
        }
        finally
        {
            isSynchronizingSelection = false;
        }
        UpdateRowSelectionSummary();
    }

    private void ClearRows_Click(object sender, RoutedEventArgs e)
    {
        isSynchronizingSelection = true;
        try
        {
            SourcePreviewGrid.SelectedItems.Clear();
            TargetPreviewGrid.SelectedItems.Clear();
        }
        finally
        {
            isSynchronizingSelection = false;
        }
        UpdateRowSelectionSummary();
    }

    private List<object[]> GetSelectedPrimaryKeys()
    {
        if (currentPrimaryKeys.Count == 0) return [];
        return SourcePreviewGrid.SelectedItems.Cast<object>()
            .Select(item => SourcePreviewGrid.Items.IndexOf(item))
            .Where(index => index >= 0 && index < currentAlignedRows.Count)
            .Select(index => currentAlignedRows[index].Source)
            .Where(row => row is not null)
            .Select(row => row!.KeyValues)
            .ToList();
    }

    private void UpdateRowSelectionSummary()
    {
        var selectedCount = GetSelectedPrimaryKeys().Count;
        var selectedRows = SourcePreviewGrid.SelectedItems.Count;
        var targetOnlyCount = Math.Max(0, selectedRows - selectedCount);
        RowSelectionSummaryText.Text = targetOnlyCount == 0
            ? $"移行するデータ: {selectedCount:N0}件選択"
            : $"移行するデータ: {selectedCount:N0}件選択（移行元にない{targetOnlyCount:N0}件は対象外）";
        if (!isBusy) MoveButton.IsEnabled = selectedCount > 0;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private async void Preflight_Click(object sender, RoutedEventArgs e) =>
        await RunCurrentTableOperationAsync("事前検査", static (engine, token) => engine.PreflightAsync(token));

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (TableList.SelectedItem is not MigrationSelectionItem selectedTable)
        {
            MessageDialogWindow.ShowMessage(this, "移行対象がありません", "左の一覧でテーブルを1件選択してください。");
            return;
        }
        var selectedKeys = GetSelectedPrimaryKeys();
        if (selectedKeys.Count == 0)
        {
            MessageDialogWindow.ShowMessage(this, "移行データがありません", "プレビューで移行するデータを1件以上選択してください。");
            return;
        }

        var backup = document.BackupBeforeCopy ? "事前バックアップ: 有効" : "事前バックアップ: 無効";
        if (!MessageDialogWindow.Confirm(
                this,
                $"{selectedKeys.Count:N0}件のデータを移行",
                $"{selectedTable.Source} → {selectedTable.TargetDisplay}\n\n" +
                $"選択した{selectedKeys.Count:N0}件を追加または更新します。\n" +
                $"未選択行の更新・削除、テーブルの全削除は行いません。\n\n{backup}"))
            return;

        await RunCurrentTableOperationAsync(
            "選択データの移行",
            (engine, token) => engine.CopySelectedRowsAsync(selectedKeys, token),
            selectedKeys.Count);
        if (TableList.SelectedItem is not null) await RefreshCurrentPreviewAsync();
    }

    private async Task RunCurrentTableOperationAsync(
        string operationName,
        Func<MigrationEngine, CancellationToken, Task> operation,
        int? rowCount = null)
    {
        if (TableList.SelectedItem is not MigrationSelectionItem selectedTable)
        {
            MessageDialogWindow.ShowMessage(this, $"{operationName}対象がありません", "左の一覧でテーブルを1件選択してください。");
            return;
        }

        previewCancellation?.Cancel();
        operationCancellation = new CancellationTokenSource();
        var token = operationCancellation.Token;
        var countText = rowCount is null ? "" : $"{rowCount:N0}件の";
        SetOperationBusy(true, $"{countText}{operationName}を実行中…");
        try
        {
            var operationDocument = CreateSingleTableDocument(selectedTable.Document);
            var configJson = JsonSerializer.Serialize(operationDocument, JsonOptions);
            await Task.Run(async () =>
            {
                var config = AppConfig.Parse(configJson);
                var engine = new MigrationEngine(config, message =>
                    Dispatcher.Invoke(() => StatusText.Text = message));
                await operation(engine, token);
            }, token);
            StatusText.Text = $"{countText}{operationName}が完了しました";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{operationName}を中止しました";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"{operationName}に失敗しました";
            MessageDialogWindow.ShowMessage(this, $"{operationName}に失敗しました", exception.Message);
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            SetOperationBusy(false, StatusText.Text);
        }
    }

    private ConfigDocument CreateSingleTableDocument(TableDocument source)
    {
        var result = new ConfigDocument
        {
            SourceConnectionString = document.SourceConnectionString,
            TargetConnectionString = document.TargetConnectionString,
            BatchSize = document.BatchSize,
            CommandTimeoutSeconds = document.CommandTimeoutSeconds,
            SourceConsistency = document.SourceConsistency,
            BackupBeforeCopy = document.BackupBeforeCopy
        };
        result.Tables.Add(new TableDocument
        {
            Source = source.Source,
            Target = source.Target,
            ColumnList = source.ColumnList is null
                ? null
                : currentPrimaryKeys.Concat(source.ColumnList)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            OperationMode = "upsertKeep",
            BackupBeforeCopy = document.BackupBeforeCopy
        });
        result.ApplyGlobalSettings();
        return result;
    }

    private void UpdateSelectionSummary()
    {
        SelectionSummaryText.Text = TableList.SelectedItem is MigrationSelectionItem selected
            ? $"選択中: {selected.Source}"
            : "1件選択してください";
        if (!isBusy)
        {
            PreflightButton.IsEnabled = TableList.SelectedItem is not null;
            MoveButton.IsEnabled = GetSelectedPrimaryKeys().Count > 0;
        }
    }

    private string DescribeEndpoints()
    {
        static string Read(string value)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(AppConfig.ExpandEnvironmentVariables(value));
                return $"{builder.DataSource} / {builder.InitialCatalog}";
            }
            catch
            {
                return "接続先未設定";
            }
        }
        return $"{Read(document.SourceConnectionString)}  →  {Read(document.TargetConnectionString)}";
    }

    private static string FormatCellValue(object value) => value switch
    {
        DBNull => "NULL",
        byte[] bytes => bytes.Length <= 24
            ? $"0x{Convert.ToHexString(bytes)}"
            : $"0x{Convert.ToHexString(bytes.AsSpan(0, 24))}… ({bytes.Length:N0} bytes)",
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private void SetPreviewBusy(bool busy, string status)
    {
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy;
        StatusText.Text = status;
    }

    private void SetOperationBusy(bool busy, string status)
    {
        isBusy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        RefreshButton.IsEnabled = !busy;
        TableList.IsEnabled = !busy;
        SelectVisibleRowsButton.IsEnabled = !busy;
        ClearRowsButton.IsEnabled = !busy;
        MoveButton.IsEnabled = !busy && GetSelectedPrimaryKeys().Count > 0;
        PreflightButton.IsEnabled = !busy && TableList.SelectedItem is not null;
        StatusText.Text = status;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "中止を要求しています…";
        operationCancellation?.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        previewCancellation?.Cancel();
        operationCancellation?.Cancel();
    }
}

public sealed class MigrationSelectionItem
{
    internal MigrationSelectionItem(TableDocument document)
    {
        Document = document;
    }

    internal TableDocument Document { get; }
    public string Source => Document.Source;
    public string TargetDisplay => string.IsNullOrWhiteSpace(Document.Target) ? Document.Source : Document.Target;
    public string OperationDisplay => "選択行を追加・更新";

}

internal sealed record TablePreview(long Count, DataTable Rows);
internal sealed record RawTablePreview(long Count, IReadOnlyList<PreviewRow> Rows);
internal sealed record PreviewRow(string AlignmentKey, string[] Cells, object[] KeyValues);
internal sealed record AlignedPreviewRow(PreviewRow? Source, PreviewRow? Target);

internal sealed class PreviewRowAscendingComparer : IComparer<PreviewRow>
{
    public static PreviewRowAscendingComparer Instance { get; } = new();

    public int Compare(PreviewRow? left, PreviewRow? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var count = Math.Min(left.KeyValues.Length, right.KeyValues.Length);
        for (var index = 0; index < count; index++)
        {
            var comparison = CompareValue(left.KeyValues[index], right.KeyValues[index]);
            if (comparison != 0) return comparison;
        }
        return left.KeyValues.Length.CompareTo(right.KeyValues.Length);
    }

    private static int CompareValue(object left, object right)
    {
        if (left is DBNull) return right is DBNull ? 0 : -1;
        if (right is DBNull) return 1;
        if (left.GetType() == right.GetType() && left is IComparable comparable)
            return comparable.CompareTo(right);
        return StringComparer.Ordinal.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture));
    }
}
internal sealed record TablePreviewPair(
    TablePreview Source,
    TablePreview Target,
    int ColumnCount,
    IReadOnlyList<string> PrimaryKeys,
    IReadOnlyList<AlignedPreviewRow> AlignedRows);
