using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipThat.Services;

namespace ClipThat.UI.Controls;

public sealed class HotkeyChangedEventArgs(uint modifiers, uint virtualKey) : EventArgs
{
    public uint Modifiers { get; } = modifiers;

    public uint VirtualKey { get; } = virtualKey;
}

/// <summary>
/// Interactive hotkey recorder displaying the active global hotkey and capturing a new
/// modifier + virtual key combination directly via <c>PreviewKeyDown</c>.
/// </summary>
public partial class HotkeyBox : UserControl
{
    private const uint ModAlt = 0x0001u;
    private const uint ModControl = 0x0002u;
    private const uint ModShift = 0x0004u;
    private const uint ModWin = 0x0008u;

    private static readonly SolidColorBrush IdleBackgroundBrush = UiBrushHelper.CreateFrozenBrush(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush IdleBorderBrush = UiBrushHelper.CreateFrozenBrush(0x33, 0xFF, 0xFF, 0xFF);

    private GlobalHotkeyService? _hotkeyService;
    private uint _currentModifiers;
    private uint _currentVirtualKey;
    private bool _isCapturing;

    public HotkeyBox()
    {
        InitializeComponent();
        Unloaded += HotkeyBox_Unloaded;
    }

    /// <summary>
    /// Raised when a new hotkey is successfully registered with Windows.
    /// </summary>
    public event EventHandler<HotkeyChangedEventArgs>? HotkeyCommitted;

    public void Initialize(GlobalHotkeyService hotkeyService, uint modifiers, uint virtualKey)
    {
        ArgumentNullException.ThrowIfNull(hotkeyService);

        _hotkeyService = hotkeyService;
        _currentModifiers = modifiers;
        _currentVirtualKey = virtualKey;
        _isCapturing = false;

        ApplyIdleVisuals();

        if (!_hotkeyService.IsRegistered)
        {
            string label = GlobalHotkeyService.FormatHotkey(_currentModifiers, _currentVirtualKey);
            ShowWarning(
                $"Nie udało się zarejestrować skrótu „{label}” — prawdopodobnie jest już używany przez inną aplikację lub system Windows.");
        }
        else
        {
            ClearWarning();
        }
    }

    public void Cleanup()
    {
        CancelCapture();
    }

    private void BeginCapture()
    {
        if (_hotkeyService is null || _isCapturing)
        {
            return;
        }

        _isCapturing = true;
        // Temporarily unregister the active shortcut so pressing it to re-bind doesn't trigger a replay save.
        _hotkeyService.Unregister();

        ClearWarning();
        CaptureBorder.SetResourceReference(Border.BackgroundProperty, "SystemAccentTintBrush");
        CaptureBorder.SetResourceReference(Border.BorderBrushProperty, "SystemAccentColorPrimaryBrush");
        HotkeyDisplayText.Text = "Naciśnij kombinację klawiszy...";
        HintText.Text = "Esc — anuluj";

        Keyboard.Focus(CaptureBorder);
    }

    private void CancelCapture()
    {
        if (!_isCapturing)
        {
            return;
        }

        _isCapturing = false;
        ApplyIdleVisuals();

        if (_hotkeyService is not null && _currentVirtualKey != 0)
        {
            bool restored = _hotkeyService.UpdateHotkey(_currentModifiers, _currentVirtualKey);
            if (!restored)
            {
                string label = GlobalHotkeyService.FormatHotkey(_currentModifiers, _currentVirtualKey);
                ShowWarning(
                    $"Nie udało się przywrócić skrótu „{label}” — prawdopodobnie jest używany przez inną aplikację.");
            }
        }
    }

    private void ApplyIdleVisuals()
    {
        CaptureBorder.Background = IdleBackgroundBrush;
        CaptureBorder.BorderBrush = IdleBorderBrush;
        HotkeyDisplayText.Text = GlobalHotkeyService.FormatHotkey(_currentModifiers, _currentVirtualKey);
        HintText.Text = "Kliknij, aby zmienić";
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = message;
        WarningBanner.Visibility = Visibility.Visible;
    }

    private void ClearWarning()
    {
        WarningText.Text = string.Empty;
        WarningBanner.Visibility = Visibility.Collapsed;
    }

    private void CaptureBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginCapture();
    }

    private void CaptureBorder_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturing)
        {
            return;
        }

        e.Handled = true;

        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        uint modifiers = ReadCurrentModifiers();

        if (actualKey == Key.Escape && modifiers == 0)
        {
            CancelCapture();
            Keyboard.ClearFocus();
            return;
        }

        if (IsModifierOnlyKey(actualKey))
        {
            HotkeyDisplayText.Text = modifiers != 0
                ? $"{GlobalHotkeyService.FormatHotkey(modifiers, 0)} + ..."
                : "Naciśnij kombinację klawiszy...";
            return;
        }

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(actualKey);
        if (virtualKey == 0)
        {
            return;
        }

        bool isFunctionKey = virtualKey >= 0x70u && virtualKey <= 0x87u;
        if (modifiers == 0 && !isFunctionKey)
        {
            ShowWarning("Użyj klawisza modyfikującego (Ctrl, Alt, Shift lub Win) albo klawisza funkcyjnego (F1–F24).");
            return;
        }

        _isCapturing = false;

        if (_hotkeyService is null)
        {
            ApplyIdleVisuals();
            return;
        }

        string attemptedLabel = GlobalHotkeyService.FormatHotkey(modifiers, virtualKey);
        if (_hotkeyService.UpdateHotkey(modifiers, virtualKey))
        {
            _currentModifiers = modifiers;
            _currentVirtualKey = virtualKey;
            ClearWarning();
            ApplyIdleVisuals();
            Keyboard.ClearFocus();
            HotkeyCommitted?.Invoke(this, new HotkeyChangedEventArgs(modifiers, virtualKey));
        }
        else
        {
            ApplyIdleVisuals();
            bool restored = _currentVirtualKey != 0 &&
                            _hotkeyService.UpdateHotkey(_currentModifiers, _currentVirtualKey);

            string restoreNote = restored
                ? $" Przywrócono poprzedni skrót ({GlobalHotkeyService.FormatHotkey(_currentModifiers, _currentVirtualKey)})."
                : string.Empty;

            ShowWarning(
                $"Nie udało się zarejestrować skrótu „{attemptedLabel}” — prawdopodobnie jest już używany przez inną aplikację lub system Windows.{restoreNote}");
            Keyboard.ClearFocus();
        }
    }

    private void CaptureBorder_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_isCapturing)
        {
            return;
        }

        e.Handled = true;
        uint modifiers = ReadCurrentModifiers();
        HotkeyDisplayText.Text = modifiers != 0
            ? $"{GlobalHotkeyService.FormatHotkey(modifiers, 0)} + ..."
            : "Naciśnij kombinację klawiszy...";
    }

    private void CaptureBorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CancelCapture();
    }

    private void HotkeyBox_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelCapture();
    }

    private static uint ReadCurrentModifiers()
    {
        ModifierKeys mods = Keyboard.Modifiers;
        uint mask = 0;

        if ((mods & ModifierKeys.Control) != 0) mask |= ModControl;
        if ((mods & ModifierKeys.Alt) != 0) mask |= ModAlt;
        if ((mods & ModifierKeys.Shift) != 0) mask |= ModShift;
        if ((mods & ModifierKeys.Windows) != 0 ||
            Keyboard.IsKeyDown(Key.LWin) ||
            Keyboard.IsKeyDown(Key.RWin))
        {
            mask |= ModWin;
        }

        return mask;
    }

    private static bool IsModifierOnlyKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System or Key.DeadCharProcessed;
}
