using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ClipThat.UI.Dialogs;

/// <summary>
/// Custom dark-themed FluentWindow confirmation dialog styled for destructive actions such as clip deletion.
/// </summary>
public partial class ConfirmDialog : FluentWindow
{
    public ConfirmDialog(
        string title,
        string message,
        string confirmText = "Usuń",
        string cancelText = "Anuluj",
        SymbolRegular icon = SymbolRegular.Delete24)
    {
        InitializeComponent();

        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        DialogIcon.Symbol = icon;

        Loaded += ConfirmDialog_Loaded;
    }

    private void ConfirmDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ConfirmDialog_Loaded;
        CancelButton.Focus();
    }

    private void HeaderArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
