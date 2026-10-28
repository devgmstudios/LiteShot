using System.Runtime.InteropServices;
using static LiteShot.Native;

namespace LiteShot;

/// <summary>
/// Global hotkeys through a low-level keyboard hook. Unlike RegisterHotKey this sees PrtScn before
/// Windows hands it to the Snipping Tool, and it can swallow the key so only LiteShot reacts.
/// The hook lives on its own thread: Windows silently removes a hook that ever answers too slowly,
/// so it must never wait on UI work such as preparing a capture.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    readonly Control invoker;
    readonly Thread thread;
    readonly ManualResetEventSlim ready = new();
    LowLevelKeyboardProc? proc;
    IntPtr handle;
    uint threadId;
    volatile Keys[] hotkeys = Array.Empty<Keys>();
    int heldVk = -1;
    static volatile Action<Keys>? interceptor;

    /// <summary>Raised on the UI thread.</summary>
    public event Action<Keys>? Pressed;

    /// <summary>
    /// While set (a hotkey box has focus), every key combination except Tab is swallowed and
    /// delivered here on the UI thread instead of triggering hotkeys.
    /// </summary>
    public static Action<Keys>? Interceptor
    {
        get => interceptor;
        set => interceptor = value;
    }

    public KeyboardHook(Control invoker)
    {
        this.invoker = invoker;
        thread = new Thread(Run) { IsBackground = true, Name = "LiteShot keyboard hook" };
        thread.Start();
        ready.Wait(3000);
    }

    public bool IsInstalled => handle != IntPtr.Zero;

    public void SetHotkeys(params Keys[] keys) => hotkeys = keys.Where(k => k != Keys.None).ToArray();

    void Run()
    {
        threadId = GetCurrentThreadId();
        proc = Callback;
        handle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (handle != IntPtr.Zero) UnhookWindowsHookEx(handle);
        handle = IntPtr.Zero;
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            int vk = (int)info.vkCode;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;

            // Swallow auto-repeat and the release of a key we already consumed.
            if (vk == heldVk)
            {
                if (!down) heldVk = -1;
                return 1;
            }

            if (down && !IsModifier(vk) && !IsDown(0x5B) && !IsDown(0x5C))
            {
                var combo = (Keys)vk | CurrentModifiers();
                if (interceptor is { } intercept && (Keys)vk != Keys.Tab)
                {
                    heldVk = vk;
                    invoker.BeginInvoke(() => intercept(combo));
                    return 1;
                }
                if (Array.IndexOf(hotkeys, combo) >= 0)
                {
                    heldVk = vk;
                    invoker.BeginInvoke(() => Pressed?.Invoke(combo));
                    return 1;
                }
            }
        }
        return CallNextHookEx(handle, nCode, wParam, lParam);
    }

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    static Keys CurrentModifiers()
    {
        var m = Keys.None;
        if (IsDown(0x11)) m |= Keys.Control;
        if (IsDown(0x10)) m |= Keys.Shift;
        if (IsDown(0x12)) m |= Keys.Alt;
        return m;
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
        ready.Dispose();
    }
}

internal static class HotkeyText
{
    public static string Format(Keys k)
    {
        if (k == Keys.None) return "None";
        var parts = new List<string>(4);
        if ((k & Keys.Control) != 0) parts.Add("Ctrl");
        if ((k & Keys.Shift) != 0) parts.Add("Shift");
        if ((k & Keys.Alt) != 0) parts.Add("Alt");
        var code = k & Keys.KeyCode;
        parts.Add(code switch
        {
            Keys.PrintScreen => "PrtScn",
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            Keys.Next => "PageDown",
            Keys.Prior => "PageUp",
            Keys.Escape => "Esc",
            Keys.Back => "Backspace",
            Keys.Oemtilde => "`",
            _ => code.ToString(),
        });
        return string.Join(" + ", parts);
    }
}
