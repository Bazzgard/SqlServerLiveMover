using System.Windows;

namespace SqlServerLiveMover.Gui;

public partial class MessageDialogWindow : Window
{
    private MessageDialogWindow(string title, string message, bool confirmation)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Content = confirmation ? "実行する" : "閉じる";
    }

    public static bool Confirm(Window owner, string title, string message)
    {
        var dialog = new MessageDialogWindow(title, message, true) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    public static void ShowMessage(Window owner, string title, string message)
    {
        var dialog = new MessageDialogWindow(title, message, false) { Owner = owner };
        _ = dialog.ShowDialog();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
