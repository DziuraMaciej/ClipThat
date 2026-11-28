using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace ClipThat.UI.Dialogs;

/// <summary>
/// Custom dark-themed FluentWindow confirmation dialog styled for destructive actions such as clip deletion.
/// </summary>
public partial class ConfirmDialog : FluentWindow
{
    private bool _isClosing;

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

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= ConfirmDialog_Loaded;
        base.OnClosed(e);
    }

    private void HeaderArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left &&
            e.ButtonState == MouseButtonState.Pressed &&
            e.ClickCount == 1)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore if the mouse button was released before WPF began the drag operation.
            }
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        CompleteDialog(result: true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CompleteDialog(result: false);
    }

    private void CompleteDialog(bool result)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;

        if (ComponentDispatcher.IsThreadModal)
        {
            DialogResult = result;
        }
        else
        {
            Close();
        }
    }
}
