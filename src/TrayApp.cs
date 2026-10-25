using System;
using System.Runtime.InteropServices;

namespace NoEnglishMode;

// Hidden window, tray icon, WinEvent hooks and the message loop.
internal static class TrayApp
{
    private const uint IDM_EXIT = 1001;
    private const uint IDM_AUTOSTART = 1002;
    private const uint TrayIconId = 1;
    private const uint TimerTrayRetry = 1;
    private const uint TrayRetryMs = 3000;
    private const string ClassName = "NoEnglishMode.HiddenWindow";
    private const string Tooltip = "NoEnglishMode - 中文模式守护";

    // Delegates passed to native code must stay alive for the process lifetime.
    private static readonly NativeMethods.WndProc WndProcRef = WndProc;
    private static readonly NativeMethods.WinEventProc EventProcRef = EventCallback;

    private static readonly ImeEnforcer Enforcer = new();

    private static IntPtr _hwnd;
    private static IntPtr _focusHook;
    private static IntPtr _foregroundHook;
    private static IntPtr _nameChangeHook;
    private static IntPtr _hIcon;
    private static uint _wmTrayCallback;
    private static uint _wmTaskbarCreated;
    private static bool _trayOk;

    public static int Run()
    {
        _wmTrayCallback = NativeMethods.WM_USER + 100;
        _wmTaskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        IntPtr instance = NativeMethods.GetModuleHandleW(null);
        if (instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetModuleHandleW failed");
        }

        _hwnd = CreateHiddenWindow(instance);
        _hIcon = LoadAppIcon(instance);

        TryAddTrayIcon();

        Enforcer.Start();

        _focusHook = InstallHook(NativeMethods.EVENT_OBJECT_FOCUS);
        _foregroundHook = InstallHook(NativeMethods.EVENT_SYSTEM_FOREGROUND);
        _nameChangeHook = InstallHook(NativeMethods.EVENT_OBJECT_NAMECHANGE);
        Logger.Info($"hooks installed: focus=0x{_focusHook.ToString("X")} foreground=0x{_foregroundHook.ToString("X")} namechange=0x{_nameChangeHook.ToString("X")}");

        ConsiderWindow(NativeMethods.GetForegroundWindow());

        int result;
        var msg = new NativeMethods.MSG();
        while ((result = NativeMethods.GetMessageW(out msg, IntPtr.Zero, 0, 0)) > 0)
        {
            _ = NativeMethods.TranslateMessage(ref msg);
            _ = NativeMethods.DispatchMessageW(ref msg);
        }

        if (result < 0)
        {
            Logger.Warn($"GetMessageW failed, win32 error {Marshal.GetLastWin32Error()}; exiting");
        }

        if (_focusHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_focusHook);
        }

        if (_foregroundHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_foregroundHook);
        }

        if (_nameChangeHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_nameChangeHook);
        }

        Enforcer.Stop();
        _ = NativeMethods.DestroyWindow(_hwnd); // WM_DESTROY removes the tray icon
        Logger.Info("exited cleanly");
        return 0;
    }

    private static IntPtr CreateHiddenWindow(IntPtr instance)
    {
        IntPtr classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var wc = new NativeMethods.WNDCLASSW
            {
                lpfnWndProc = WndProcRef,
                hInstance = instance,
                lpszClassName = classNamePtr,
            };

            ushort atom = NativeMethods.RegisterClassW(ref wc);
            if (atom == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassW failed, win32 error {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr); // RegisterClassW copies the string
        }

        IntPtr hwnd = NativeMethods.CreateWindowExW(
            0,
            ClassName,
            "NoEnglishMode",
            0,
            0, 0, 0, 0,
            IntPtr.Zero, // top-level: required to receive TaskbarCreated broadcasts
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed, win32 error {Marshal.GetLastWin32Error()}");
        }

        return hwnd;
    }

    private static IntPtr LoadAppIcon(IntPtr instance)
    {
        string exePath = typeof(TrayApp).Assembly.Location;
        if (!string.IsNullOrEmpty(exePath))
        {
            IntPtr extracted = NativeMethods.ExtractIconW(IntPtr.Zero, exePath, 0);
            if (extracted != IntPtr.Zero && extracted != (IntPtr)1)
            {
                return extracted;
            }
        }

        IntPtr loaded = NativeMethods.LoadIconW(instance, (IntPtr)1); // app icon resource
        return loaded != IntPtr.Zero
            ? loaded
            : NativeMethods.LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    private static void TryAddTrayIcon()
    {
        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = _wmTrayCallback,
            hIcon = _hIcon,
            szTip = Tooltip.Length > 127 ? Tooltip.Substring(0, 127) : Tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid); // clear a stale entry
        if (NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid))
        {
            _trayOk = true;
            _ = NativeMethods.KillTimer(_hwnd, (UIntPtr)TimerTrayRetry);
            Logger.Info("tray icon added");
        }
        else
        {
            _trayOk = false;
            _ = NativeMethods.SetTimer(_hwnd, (UIntPtr)TimerTrayRetry, TrayRetryMs, IntPtr.Zero);
            Logger.Warn($"Shell_NotifyIconW(NIM_ADD) failed, win32 error {Marshal.GetLastWin32Error()} - retrying every {TrayRetryMs} ms");
        }
    }

    private static void RemoveTrayIcon()
    {
        if (!_trayOk)
        {
            return;
        }

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
        _trayOk = false;
    }

    private static void ShowContextMenu(int x, int y)
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            uint autoStartFlags = AutoStart.IsEnabled() ? NativeMethods.MfChecked : 0; // MF_STRING is 0
            _ = NativeMethods.AppendMenuW(menu, autoStartFlags, (UIntPtr)IDM_AUTOSTART, "开机自启");
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
            _ = NativeMethods.AppendMenuW(menu, 0, (UIntPtr)IDM_EXIT, "退出");

            _ = NativeMethods.SetForegroundWindow(_hwnd);
            _ = NativeMethods.TrackPopupMenu(
                menu, NativeMethods.TPM_RIGHTBUTTON, x, y, 0, _hwnd, IntPtr.Zero);
            _ = NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private static IntPtr InstallHook(uint eventId)
    {
        IntPtr hook = NativeMethods.SetWinEventHook(
            eventId,
            eventId,
            IntPtr.Zero,
            EventProcRef,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (hook == IntPtr.Zero)
        {
            Logger.Warn($"SetWinEventHook(0x{eventId.ToString("X")}) failed, win32 error {Marshal.GetLastWin32Error()}");
        }

        return hook;
    }

    private static void EventCallback(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        try
        {
            if (eventType == NativeMethods.EVENT_OBJECT_NAMECHANGE)
            {
                if (NativeMethods.IsInputIndicatorWindow(hwnd))
                {
                    Enforcer.OnImeIndicatorChanged();
                }

                return;
            }

            ConsiderWindow(hwnd);
        }
        catch
        {
        }
    }

    private static void ConsiderWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            uint tid = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            if (tid == 0)
            {
                return;
            }

            IntPtr hkl = NativeMethods.GetKeyboardLayout(tid);
            if ((unchecked((uint)hkl.ToInt64()) & 0xFFFF) == 0x0804)
            {
                Enforcer.OnFocusChanged(hwnd);
            }
        }
        catch
        {
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated)
            {
                TryAddTrayIcon();
                return IntPtr.Zero;
            }

            if (msg == _wmTrayCallback)
            {
                uint mouse = unchecked((uint)lParam.ToInt64());
                if (mouse == NativeMethods.WM_RBUTTONUP)
                {
                    if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
                    {
                        ShowContextMenu(pt.X, pt.Y);
                    }
                }
                else if (mouse == NativeMethods.WM_CONTEXTMENU)
                {
                    long lp = lParam.ToInt64();
                    int x = unchecked((short)(lp & 0xFFFF));
                    int y = unchecked((short)((lp >> 16) & 0xFFFF));
                    ShowContextMenu(x, y);
                }

                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_TIMER && (uint)wParam.ToInt64() == TimerTrayRetry)
            {
                if (!_trayOk)
                {
                    TryAddTrayIcon();
                }

                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_COMMAND)
            {
                uint commandId = (uint)wParam.ToInt64();
                if (commandId == IDM_EXIT)
                {
                    Logger.Info("exit requested from tray menu");
                    NativeMethods.PostQuitMessage(0);
                }
                else if (commandId == IDM_AUTOSTART)
                {
                    AutoStart.SetEnabled(!AutoStart.IsEnabled());
                }

                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_QUERYENDSESSION || msg == NativeMethods.WM_ENDSESSION)
            {
                return (IntPtr)1;
            }

            if (msg == NativeMethods.WM_DESTROY)
            {
                RemoveTrayIcon();
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Logger.Error("error in WndProc", ex);
            return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
