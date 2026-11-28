using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClipThat.Services;

/// <summary>
/// Registers and listens for a global Win32 hotkey via a lightweight message-only window
/// (<c>HWND_MESSAGE</c>) so hotkeys fire reliably even when no WPF window is open and while
/// fullscreen games are in the foreground.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ReplayHotkeyId = 0x4354; // "CT"
    private const uint ModNoRepeat = 0x4000u;
    private const uint ValidModifierMask = 0x000Fu; // MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN
    private static readonly nint HwndMessageParent = new(-3);

    private readonly Dispatcher _dispatcher;

    private HwndSource? _hwndSource;
    private uint _currentModifiers;
    private uint _currentVirtualKey;
    private bool _isRegistered;
    private int _disposed;

    public GlobalHotkeyService(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        InvokeOnDispatcher(CreateMessageWindow);
    }

    /// <summary>
    /// Raised on the dispatcher thread whenever the registered global hotkey is pressed.
    /// </summary>
    public event EventHandler? HotkeyPressed;

    public uint CurrentModifiers => _currentModifiers;

    public uint CurrentVirtualKey => _currentVirtualKey;

    public bool IsRegistered => _isRegistered;

    public int LastWin32Error { get; private set; }

    /// <summary>
    /// Unregisters any previously registered hotkey and registers the supplied Win32 modifier mask
    /// and virtual key code (for example <c>MOD_ALT = 0x0001</c> + <c>VK_F10 = 0x79</c>).
    /// Returns <see langword="true"/> when registration succeeds.
    /// </summary>
    public bool UpdateHotkey(uint modifiers, uint virtualKey)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return InvokeOnDispatcher(() => UpdateHotkeyOnDispatcher(modifiers, virtualKey));
    }

    /// <summary>
    /// Unregisters the currently active global hotkey if one is registered.
    /// </summary>
    public void Unregister()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        InvokeOnDispatcher(UnregisterOnDispatcher);
    }

    /// <summary>
    /// Formats a Win32 modifier mask and virtual key code into a readable label (e.g. <c>Alt + F10</c>).
    /// </summary>
    public static string FormatHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(5);
        if ((modifiers & 0x0002u) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001u) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004u) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008u) != 0) parts.Add("Win");

        if (virtualKey != 0)
        {
            string keyLabel = FormatVirtualKey(virtualKey);
            parts.Add(keyLabel);
        }

        return parts.Count > 0 ? string.Join(" + ", parts) : "Brak";
    }

    private static string FormatVirtualKey(uint virtualKey)
    {
        return virtualKey switch
        {
            >= 0x70u and <= 0x87u => $"F{virtualKey - 0x6Fu}",
            >= 0x30u and <= 0x39u => ((char)virtualKey).ToString(),
            >= 0x41u and <= 0x5Au => ((char)virtualKey).ToString(),
            >= 0x60u and <= 0x69u => $"Num {virtualKey - 0x60u}",
            0x6Au => "Num *",
            0x6Bu => "Num +",
            0x6Du => "Num -",
            0x6Eu => "Num .",
            0x6Fu => "Num /",
            0x20u => "Spacja",
            0x08u => "Backspace",
            0x09u => "Tab",
            0x0Du => "Enter",
            0x2Cu => "PrintScreen",
            0x13u => "Pause",
            0x91u => "ScrollLock",
            0x2Du => "Insert",
            0x2Eu => "Delete",
            0x24u => "Home",
            0x23u => "End",
            0x21u => "PageUp",
            0x22u => "PageDown",
            0x25u => "←",
            0x26u => "↑",
            0x27u => "→",
            0x28u => "↓",
            0xBAu => ";",
            0xBBu => "=",
            0xBCu => ",",
            0xBDu => "-",
            0xBEu => ".",
            0xBFu => "/",
            0xC0u => "`",
            0xDBu => "[",
            0xDCu => "\\",
            0xDDu => "]",
            0xDEu => "'",
            _ => FormatFallbackVirtualKey(virtualKey)
        };
    }

    private static string FormatFallbackVirtualKey(uint virtualKey)
    {
        Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        return key != Key.None ? key.ToString() : $"VK_0x{virtualKey:X2}";
    }

    private void CreateMessageWindow()
    {
        if (_hwndSource is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("ClipThatGlobalHotkeyHost")
        {
            ParentWindow = HwndMessageParent,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    private bool UpdateHotkeyOnDispatcher(uint modifiers, uint virtualKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        CreateMessageWindow();
        UnregisterOnDispatcher();

        _currentModifiers = modifiers & ValidModifierMask;
        _currentVirtualKey = virtualKey;
        LastWin32Error = 0;

        if (_hwndSource is null || _hwndSource.Handle == nint.Zero || virtualKey == 0)
        {
            return false;
        }

        uint nativeModifiers = _currentModifiers | ModNoRepeat;
        bool registered = RegisterHotKey(_hwndSource.Handle, ReplayHotkeyId, nativeModifiers, virtualKey);
        if (!registered)
        {
            LastWin32Error = Marshal.GetLastWin32Error();
            Debug.WriteLine(
                $"Nie udało się zarejestrować skrótu globalnego {FormatHotkey(_currentModifiers, virtualKey)} (Win32 error: {LastWin32Error}).");
            _isRegistered = false;
            return false;
        }

        _isRegistered = true;
        return true;
    }

    private void UnregisterOnDispatcher()
    {
        if (_isRegistered && _hwndSource is not null && _hwndSource.Handle != nint.Zero)
        {
            UnregisterHotKey(_hwndSource.Handle, ReplayHotkeyId);
        }

        _isRegistered = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == (nint)ReplayHotkeyId && Volatile.Read(ref _disposed) == 0)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            InvokeOnDispatcher(DisposeOnDispatcher);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd zwalniania GlobalHotkeyService: {ex.Message}");
        }
    }

    private void DisposeOnDispatcher()
    {
        UnregisterOnDispatcher();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    private void InvokeOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.Invoke(action);
    }

    private T InvokeOnDispatcher<T>(Func<T> func)
    {
        if (_dispatcher.CheckAccess())
        {
            return func();
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return default!;
        }

        return _dispatcher.Invoke(func);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
