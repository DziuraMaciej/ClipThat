using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace ClipThat.UI.Dialogs;

public enum ConfirmDialogTone
{
    Destructive,
    Accent,
    Success
}

/// <summary>
/// Custom dark-themed FluentWindow confirmation/information dialog supporting destructive,
/// accent (update/action), and success tones as well as single-button info mode.
/// </summary>
public partial class ConfirmDialog : FluentWindow
{
    private static readonly SolidColorBrush AccentIconBg = UiBrushHelper.CreateFrozenBrush(0x26, 0x3B, 0x82, 0xF6);
    private static readonly SolidColorBrush AccentIconBorder = UiBrushHelper.CreateFrozenBrush(0x40, 0x60, 0xA5, 0xFA);
    private static readonly SolidColorBrush AccentIconFg = UiBrushHelper.CreateFrozenBrush(0xFF, 0x60, 0xA5, 0xFA);

    private static readonly SolidColorBrush SuccessIconBg = UiBrushHelper.CreateFrozenBrush(0x26, 0x22, 0xC5, 0x5E);
    private static readonly SolidColorBrush SuccessIconBorder = UiBrushHelper.CreateFrozenBrush(0x40, 0x4A, 0xDE, 0x80);
    private static readonly SolidColorBrush SuccessIconFg = UiBrushHelper.CreateFrozenBrush(0xFF, 0x4A, 0xDE, 0x80);

    private static readonly SolidColorBrush PrimaryButtonBg = UiBrushHelper.CreateFrozenBrush(0xFF, 0x25, 0x63, 0xEB);
    private static readonly SolidColorBrush PrimaryButtonHoverBg = UiBrushHelper.CreateFrozenBrush(0xFF, 0x1D, 0x4E, 0xD8);
    private static readonly SolidColorBrush PrimaryButtonPressedBg = UiBrushHelper.CreateFrozenBrush(0xFF, 0x1E, 0x40, 0xAF);
    private static readonly SolidColorBrush PrimaryButtonBorder = UiBrushHelper.CreateFrozenBrush(0xFF, 0x3B, 0x82, 0xF6);
    private static readonly SolidColorBrush PrimaryButtonHoverBorder = UiBrushHelper.CreateFrozenBrush(0xFF, 0x25, 0x63, 0xEB);
    private static readonly SolidColorBrush PrimaryButtonPressedBorder = UiBrushHelper.CreateFrozenBrush(0xFF, 0x1E, 0x40, 0xAF);

    private readonly ConfirmDialogTone _tone;
    private readonly bool _hasCancelButton;
    private bool _isClosing;

    public ConfirmDialog(
        string title,
        string message,
        string confirmText = "Usuń",
        string? cancelText = "Anuluj",
        SymbolRegular icon = SymbolRegular.Delete24,
        ConfirmDialogTone tone = ConfirmDialogTone.Destructive)
    {
        InitializeComponent();

        _tone = tone;
        _hasCancelButton = !string.IsNullOrWhiteSpace(cancelText);

        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        DialogIcon.Symbol = icon;

        if (_hasCancelButton)
        {
            CancelButton.Content = cancelText;
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.IsCancel = true;
            ConfirmButton.IsDefault = tone != ConfirmDialogTone.Destructive;
        }
        else
        {
            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsCancel = false;
            ConfirmButton.IsDefault = true;
            ConfirmButton.IsCancel = true;
        }

        ApplyTone(tone);
        Loaded += ConfirmDialog_Loaded;
    }

    private void ApplyTone(ConfirmDialogTone tone)
    {
        switch (tone)
        {
            case ConfirmDialogTone.Accent:
                DialogIconBorder.Background = AccentIconBg;
                DialogIconBorder.BorderBrush = AccentIconBorder;
                DialogIcon.Foreground = AccentIconFg;
                ApplyPrimaryConfirmButtonStyle();
                break;

            case ConfirmDialogTone.Success:
                DialogIconBorder.Background = SuccessIconBg;
                DialogIconBorder.BorderBrush = SuccessIconBorder;
                DialogIcon.Foreground = SuccessIconFg;
                ApplyPrimaryConfirmButtonStyle();
                break;

            case ConfirmDialogTone.Destructive:
            default:
                // XAML defaults already style the destructive red theme.
                break;
        }
    }

    private void ApplyPrimaryConfirmButtonStyle()
    {
        ConfirmButton.Background = PrimaryButtonBg;
        ConfirmButton.MouseOverBackground = PrimaryButtonHoverBg;
        ConfirmButton.PressedBackground = PrimaryButtonPressedBg;
        ConfirmButton.BorderBrush = PrimaryButtonBorder;
        ConfirmButton.MouseOverBorderBrush = PrimaryButtonHoverBorder;
        ConfirmButton.PressedBorderBrush = PrimaryButtonPressedBorder;
    }

    private void ConfirmDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ConfirmDialog_Loaded;
        if (_hasCancelButton && _tone == ConfirmDialogTone.Destructive)
        {
            CancelButton.Focus();
        }
        else
        {
            ConfirmButton.Focus();
        }
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
