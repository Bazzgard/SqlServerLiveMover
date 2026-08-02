using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace SqlServerLiveMover.Gui;

public partial class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal ConfigDocument Document { get; private set; }
    internal string? ConfigPath { get; private set; }

    internal SettingsWindow(ConfigDocument source, string? configPath)
    {
        InitializeComponent();
        Document = Clone(source);
        ConfigPath = configPath;
        DataContext = Document;
        UpdateConfigPath();
    }

    private static ConfigDocument Clone(ConfigDocument source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        var result = JsonSerializer.Deserialize<ConfigDocument>(json, JsonOptions)
                     ?? throw new InvalidOperationException("設定を複製できませんでした。");
        result.NormalizeAfterLoad();
        return result;
    }

    private void LoadConfig_Click(object sender, RoutedEventArgs e)
    {
        var previousDirectory = ConfigPath is null ? null : Path.GetDirectoryName(ConfigPath);
        var dialog = new OpenFileDialog
        {
            Title = "移行設定を開く",
            Filter = "JSON設定 (*.json)|*.json|すべてのファイル (*.*)|*.*",
            InitialDirectory = Directory.Exists(previousDirectory)
                ? previousDirectory
                : AppContext.BaseDirectory,
            FileName = ConfigPath is null ? "" : Path.GetFileName(ConfigPath),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<ConfigDocument>(
                             File.ReadAllText(dialog.FileName), JsonOptions)
                         ?? throw new InvalidOperationException("設定ファイルを読み取れませんでした。");
            loaded.Tables ??= [];
            loaded.NormalizeAfterLoad();
            Document = loaded;
            ConfigPath = dialog.FileName;
            DataContext = Document;
            UpdateConfigPath();
        }
        catch (Exception exception)
        {
            MessageDialogWindow.ShowMessage(this, "設定の読み込みに失敗しました", exception.Message);
        }
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (ConfigPath is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "移行設定を保存",
                Filter = "JSON設定 (*.json)|*.json",
                FileName = "mover.json",
                InitialDirectory = AppContext.BaseDirectory,
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog(this) != true) return;
            ConfigPath = dialog.FileName;
        }

        try
        {
            Document.ApplyGlobalSettings();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Document, JsonOptions));
            UpdateConfigPath();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageDialogWindow.ShowMessage(this, "設定の保存に失敗しました", exception.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdateConfigPath() => ConfigPathText.Text = ConfigPath ?? "未保存";
}
