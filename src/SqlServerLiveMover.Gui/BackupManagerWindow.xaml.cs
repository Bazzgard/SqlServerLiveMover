using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace SqlServerLiveMover.Gui;

public partial class BackupManagerWindow : Window
{
    private readonly string connectionString;
    private readonly BackupService service;
    private bool isBusy;

    public ObservableCollection<BackupEntry> Backups { get; } = [];

    public BackupManagerWindow(string connectionString)
    {
        InitializeComponent();
        this.connectionString = connectionString;
        service = new BackupService(120, message => Dispatcher.Invoke(() => AppendLog(message)));
        DataContext = this;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppendLog($"ローカル管理カタログ: {LocalBackupCatalogStore.CatalogPath}");
        await RefreshAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        SetBusy(true, "一覧を取得中...");
        try
        {
            var entries = await service.ListAsync(connectionString, CancellationToken.None);
            Backups.Clear();
            foreach (var entry in entries) Backups.Add(entry);
            SetBusy(false, $"{Backups.Count}件のバックアップ");
        }
        catch (Exception exception)
        {
            SetBusy(false, "一覧取得に失敗");
            ShowError("バックアップ一覧を取得できません", exception.Message);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupEntry entry) return;
        var backupBeforeRestore = BackupBeforeRestoreCheckBox.IsChecked == true;
        var safetyMessage = backupBeforeRestore
            ? "現在の復元先データは、復元直前に別のバックアップとして保存されます。"
            : "注意: 現在の復元先データはバックアップされず、復元後に元へ戻せない可能性があります。";
        var message = $"{entry.BackupQualifiedName} を {entry.TargetQualifiedName} へ復元します。\n\n" +
                      safetyMessage + "\n\n続行しますか？";
        if (!MessageDialogWindow.Confirm(this, "バックアップの復元", message))
            return;

        SetBusy(true, "復元中...");
        try
        {
            var rows = await service.RestoreAsync(
                connectionString, entry, backupBeforeRestore, CancellationToken.None);
            AppendLog($"復元完了: {rows:N0}行");
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            SetBusy(false, "復元に失敗");
            ShowError("復元に失敗しました", exception.Message);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupEntry entry) return;
        if (!MessageDialogWindow.Confirm(
                this, "バックアップの削除",
                $"{entry.BackupQualifiedName} を完全に削除します。元に戻せません。続行しますか？"))
            return;

        SetBusy(true, "削除中...");
        try
        {
            await service.DeleteAsync(connectionString, entry, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            SetBusy(false, "削除に失敗");
            ShowError("削除に失敗しました", exception.Message);
        }
    }

    private void BackupsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool value, string status)
    {
        isBusy = value;
        BusyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = BackupsGrid.SelectedItem is BackupEntry;
        RestoreButton.IsEnabled = selected && !isBusy;
        DeleteButton.IsEnabled = selected && !isBusy;
    }

    private void AppendLog(string message)
    {
        ManagerLog.AppendText(message + Environment.NewLine);
        ManagerLog.ScrollToEnd();
    }

    private void ShowError(string title, string message) =>
        MessageDialogWindow.ShowMessage(this, title, message);
}
