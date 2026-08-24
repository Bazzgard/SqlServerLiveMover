using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Data.SqlClient;

namespace SqlServerLiveMover.Gui;

public partial class MainWindow : Window
{
    private static readonly string LastSessionPath = Path.Combine(
        AppContext.BaseDirectory, "logs", "last-session.json");
    private static readonly string LastConfigPathFile = Path.Combine(
        AppContext.BaseDirectory, "logs", "last-config-path.txt");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private ConfigDocument document;
    private string? configPath;
    private CancellationTokenSource? operationCancellation;

    public MainWindow()
    {
        InitializeComponent();
        document = LoadLastSessionOrDefault();
        configPath = LoadLastConfigPath();
        document.NormalizeAfterLoad();
        DataContext = document;
        UpdateServerDisplay();
        AppendLog(File.Exists(LastSessionPath)
            ? $"前回の画面内容を読み込みました: {LastSessionPath}"
            : "設定を入力し、「事前検査」から開始してください。");
    }

    private static ConfigDocument CreateDefaultDocument()
    {
        var result = new ConfigDocument();
        result.Tables.Add(CreateDefaultTable());
        return result;
    }

    private static TableDocument CreateDefaultTable() => new() { OperationMode = "emptyOnly" };

    private void AddTable_Click(object sender, RoutedEventArgs e)
    {
        document.Tables.Add(CreateDefaultTable());
        TablesGrid.SelectedIndex = document.Tables.Count - 1;
        TablesGrid.ScrollIntoView(TablesGrid.SelectedItem);
    }

    private void RemoveTable_Click(object sender, RoutedEventArgs e)
    {
        if (TablesGrid.SelectedItem is TableDocument selected)
            document.Tables.Remove(selected);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var window = new SettingsWindow(document, configPath) { Owner = this };
        if (window.ShowDialog() != true) return;

        document = window.Document;
        configPath = window.ConfigPath;
        document.NormalizeAfterLoad();
        DataContext = document;
        UpdateServerDisplay();
        SaveLastConfigPath();
        SaveLastSession();
        AppendLog(configPath is null
            ? "接続と実行設定を更新しました。"
            : $"接続と実行設定を更新しました: {configPath}");
    }

    private void BackupManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var connectionString = AppConfig.ExpandEnvironmentVariables(document.TargetConnectionString);
            var window = new BackupManagerWindow(connectionString) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            ShowError("バックアップ管理を開けません", exception.Message);
        }
    }

    private void CompareMigration_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        SaveLastSession();
        var window = new CompareMigrationWindow(document) { Owner = this };
        window.ShowDialog();
    }

    private async void Preflight_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("事前検査", static (engine, token) => engine.PreflightAsync(token));

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var selectedTables = GetSelectedTables();
        if (selectedTables.Count == 0)
        {
            ShowError("コピー対象がありません", "テーブル一覧で対象にするテーブルへチェックを付けてください。");
            return;
        }
        var destructiveTargets = selectedTables
            .Where(table => table.OperationMode == "replace")
            .Select(table => string.IsNullOrWhiteSpace(table.Target) ? table.Source : table.Target)
            .ToList();
        var backupTargets = selectedTables
            .Where(table => table.BackupBeforeCopy && table.OperationMode != "emptyOnly")
            .Select(table => string.IsNullOrWhiteSpace(table.Target) ? table.Source : table.Target)
            .ToList();
        var deleteTargets = selectedTables
            .Where(table => table.OperationMode == "upsertDelete")
            .Select(table => string.IsNullOrWhiteSpace(table.Target) ? table.Source : table.Target)
            .ToList();
        var detail = $"チェックされた{selectedTables.Count:N0}テーブルを対象に、選択された処理方式でコピー／差分更新を実行します。";
        if (destructiveTargets.Count > 0)
            detail += "\n\n次の移行先テーブルをトランザクション内で空にしてからコピーします:\n\n" +
                      string.Join("\n", destructiveTargets);
        if (backupTargets.Count > 0)
            detail += "\n\n既存データがある場合は、次のテーブルを事前バックアップします:\n\n" +
                      string.Join("\n", backupTargets);
        if (deleteTargets.Count > 0)
            detail += "\n\n注意: 次のテーブルでは移行元に存在しない移行先行を削除します:\n\n" +
                      string.Join("\n", deleteTargets);
        if (!MessageDialogWindow.Confirm(
                this, "コピー実行の確認", detail + "\n\nコピーを開始しますか？"))
            return;

        await RunOperationAsync("コピー", static (engine, token) => engine.CopyAsync(token));
    }

    private async void Verify_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("件数検証", static (engine, token) => engine.VerifyAsync(token));

    private async Task RunOperationAsync(
        string operationName,
        Func<MigrationEngine, CancellationToken, Task> operation)
    {
        CommitGridEdits();
        var selectedTables = GetSelectedTables();
        if (selectedTables.Count == 0)
        {
            ShowError($"{operationName}対象がありません", "テーブル一覧で対象にするテーブルへチェックを付けてください。");
            return;
        }
        SaveLastSession();
        SetBusy(true, $"{operationName}を実行中...");
        AppendLog($"\n[{DateTime.Now:HH:mm:ss}] {operationName}を開始");
        operationCancellation = new CancellationTokenSource();

        try
        {
            var operationDocument = CreateOperationDocument(selectedTables);
            var configJson = JsonSerializer.Serialize(operationDocument, JsonOptions);
            var token = operationCancellation.Token;
            await Task.Run(async () =>
            {
                var config = AppConfig.Parse(configJson);
                var engine = new MigrationEngine(config, message => Dispatcher.Invoke(() => AppendLog(message)));
                await operation(engine, token);
            }, token);
            StatusText.Text = $"{operationName}が完了しました";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {operationName}が完了");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "処理を中止しました";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ユーザーにより中止");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"{operationName}に失敗しました";
            AppendLog($"エラー: {exception.Message}");
            ShowError($"{operationName}に失敗しました", exception.Message);
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            SetBusy(false, StatusText.Text);
        }
    }

    private List<TableDocument> GetSelectedTables() => document.Tables
        .Where(table => table.IsSelectedForCopy)
        .ToList();

    private ConfigDocument CreateOperationDocument(IEnumerable<TableDocument> selectedTables)
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
        foreach (var table in selectedTables) result.Tables.Add(table);
        result.ApplyGlobalSettings();
        return result;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "中止を要求しています...";
        operationCancellation?.Cancel();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

    private void UpdateServerDisplay()
    {
        var source = ReadEndpoint(document.SourceConnectionString);
        var target = ReadEndpoint(document.TargetConnectionString);
        SourceServerText.Text = source.Server;
        SourceDatabaseText.Text = $"データベース: {source.Database}";
        TargetServerText.Text = target.Server;
        TargetDatabaseText.Text = $"データベース: {target.Database}";
    }

    private static (string Server, string Database) ReadEndpoint(string connectionString)
    {
        try
        {
            var expanded = AppConfig.ExpandEnvironmentVariables(connectionString);
            var builder = new SqlConnectionStringBuilder(expanded);
            return (
                string.IsNullOrWhiteSpace(builder.DataSource) ? "未設定" : builder.DataSource,
                string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "未設定" : builder.InitialCatalog);
        }
        catch (ConfigException)
        {
            return ("環境変数未設定", "未設定");
        }
        catch (ArgumentException)
        {
            return ("接続文字列エラー", "未設定");
        }
    }

    private void CommitGridEdits()
    {
        TablesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        TablesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        document.ApplyGlobalSettings();
    }

    private ConfigDocument LoadLastSessionOrDefault()
    {
        if (!File.Exists(LastSessionPath)) return CreateDefaultDocument();
        try
        {
            return JsonSerializer.Deserialize<ConfigDocument>(File.ReadAllText(LastSessionPath), JsonOptions)
                   ?? CreateDefaultDocument();
        }
        catch
        {
            return CreateDefaultDocument();
        }
    }

    private static string? LoadLastConfigPath()
    {
        try
        {
            if (!File.Exists(LastConfigPathFile)) return null;
            var path = File.ReadAllText(LastConfigPathFile).Trim();
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveLastConfigPath()
    {
        if (string.IsNullOrWhiteSpace(configPath)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastConfigPathFile)!);
            File.WriteAllText(LastConfigPathFile, configPath);
        }
        catch (Exception exception)
        {
            AppendLog($"前回使用した設定ファイルを記録できませんでした: {exception.Message}");
        }
    }

    private void SaveLastSession()
    {
        try
        {
            document.ApplyGlobalSettings();
            Directory.CreateDirectory(Path.GetDirectoryName(LastSessionPath)!);
            var temporary = LastSessionPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, LastSessionPath, true);
        }
        catch (Exception exception)
        {
            AppendLog($"前回設定を保存できませんでした: {exception.Message}");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CommitGridEdits();
        SaveLastSession();
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void SetBusy(bool isBusy, string status)
    {
        BusyProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = isBusy;
        PreflightButton.IsEnabled = !isBusy;
        CopyButton.IsEnabled = !isBusy;
        VerifyButton.IsEnabled = !isBusy;
        StatusText.Text = status;
    }

    private void ShowError(string title, string message) =>
        MessageDialogWindow.ShowMessage(this, title, message);
}
