using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NoEnglishMode;

// Keeps the Simplified Chinese IME (keyboard layout 0x0804) in Chinese
// conversion mode:
//  - focus events start a bounded verification of the focused window;
//  - webview windows (Chromium family, Firefox) keep a passive guard after
//    verification: page-internal focus moves produce no Win32 events, so the
//    worker waits for input-indicator rename events instead;
//  - indicator events also trigger re-verification, covering manual 中/英
//    switches; non-zh-CN layouts are never touched.
// All IME traffic goes through SendMessageTimeoutW; writes are rate-limited.
internal sealed class ImeEnforcer
{
    private const int WatchTotalMs = 2000;
    private const int FastPhaseMs = 600;
    private const int FastPollMs = 40;
    private const int SlowPollMs = 120;
    private const int StableReadsToStop = 2;
    private const int MaxWritesPerWatch = 50;
    private const int ReadTimeoutMs = 200;
    private const int WriteTimeoutMs = 500;

    private const uint LangIdZhCn = 0x0804;
    private const uint ChineseConversionMode = 1025; // MS Pinyin Chinese mode
    private const uint ImeCmodeNative = 0x0001;      // IME_CMODE_NATIVE

    private const int IndicatorDebounceMs = 150;
    private const int WebViewMinWriteIntervalMs = 2000;
    private const int WebViewWarnEveryWrites = 25;
    private const int WebViewFallbackPollMs = 0; // 0 = event-only

    private enum CheckResult
    {
        Stable,
        Retry,
        Skip,
    }

    private readonly object _gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly AutoResetEvent _wake = new(false);

    private IntPtr _focusHwnd;
    private bool _focusIsWebview;
    private int _version; // bumped by focus and indicator events
    private int _handledVersion;
    private long _lastIndicatorMs = -1_000_000;
    private volatile bool _stopping;
    private Thread? _thread;

    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ImeEnforcer",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(1500);
    }

    public void OnFocusChanged(IntPtr hwnd)
    {
        bool webview = NativeMethods.LooksLikeWebView(hwnd);
        lock (_gate)
        {
            _focusHwnd = hwnd;
            _focusIsWebview = webview;
            unchecked { _version++; }
        }

        _wake.Set();
    }

    public void OnImeIndicatorChanged()
    {
        lock (_gate)
        {
            long now = Clock.ElapsedMilliseconds;
            if (now - _lastIndicatorMs < IndicatorDebounceMs)
            {
                return;
            }

            _lastIndicatorMs = now;
            unchecked { _version++; }
        }

        _wake.Set();
    }

    private void MarkHandled()
    {
        lock (_gate)
        {
            _handledVersion = _version;
        }
    }

    private void Run()
    {
        while (!_stopping)
        {
            IntPtr target;
            bool isWebview;
            int version;
            lock (_gate)
            {
                target = _focusHwnd;
                isWebview = _focusIsWebview;
                version = _version;
            }

            if (target == IntPtr.Zero || version == _handledVersion)
            {
                // a pending Set() is latched, so an infinite wait cannot miss
                // a wake-up
                _wake.WaitOne(Timeout.Infinite);
                continue;
            }

            try
            {
                WatchWindow(target, isWebview);
            }
            catch (Exception ex)
            {
                Logger.Error("watch loop error", ex);
                MarkHandled();
            }
        }
    }

    private void WatchWindow(IntPtr hwnd, bool webview)
    {
        string desc = DescribeWindow(hwnd);

        long start = Clock.ElapsedMilliseconds;
        long deadline = start + WatchTotalMs;
        int entryVersion;
        lock (_gate)
        {
            entryVersion = _version;
        }

        int stable = 0;
        int writes = 0;
        bool everStable = false;
        bool gaveUpLogged = false;
        bool passive = false; // webview only: verified, waiting for indicator events
        long lastWriteMs = -1;

        while (true)
        {
            if (_stopping)
            {
                return;
            }

            IntPtr current;
            int version;
            lock (_gate)
            {
                current = _focusHwnd;
                version = _version;
            }

            if (current != hwnd)
            {
                // superseded by a newer focus target; do not mark handled
                return;
            }

            if (version != entryVersion)
            {
                entryVersion = version;
                start = Clock.ElapsedMilliseconds;
                deadline = start + WatchTotalMs;
                stable = 0;
                passive = false;
            }

            if (!NativeMethods.IsWindow(hwnd))
            {
                MarkHandled();
                return;
            }

            if (!passive)
            {
                long now = Clock.ElapsedMilliseconds;
                if (now >= deadline)
                {
                    if (!webview)
                    {
                        break;
                    }

                    passive = true;
                    if (!everStable)
                    {
                        Logger.Warn($"could not confirm Chinese mode for {desc} within {WatchTotalMs} ms (writes={writes}); switching to event-driven guard");
                    }
                }
                else
                {
                    switch (CheckAndFix(hwnd, desc, webview, ref writes, ref gaveUpLogged, ref lastWriteMs, now))
                    {
                        case CheckResult.Skip:
                            MarkHandled();
                            return;
                        case CheckResult.Stable:
                            everStable = true;
                            if (++stable >= StableReadsToStop)
                            {
                                if (webview)
                                {
                                    passive = true;
                                }
                                else
                                {
                                    MarkHandled();
                                    return;
                                }
                            }

                            break;
                        default:
                            stable = 0;
                            break;
                    }
                }
            }

            int wait;
            if (passive)
            {
                wait = WebViewFallbackPollMs > 0 ? WebViewFallbackPollMs : Timeout.Infinite;
            }
            else
            {
                int elapsed = (int)(Clock.ElapsedMilliseconds - start);
                wait = elapsed < FastPhaseMs ? FastPollMs : SlowPollMs;
            }

            _wake.WaitOne(wait);
        }

        if (!everStable)
        {
            Logger.Warn($"could not confirm Chinese mode for {desc} within {WatchTotalMs} ms (writes={writes})");
        }

        MarkHandled();
    }

    private static CheckResult CheckAndFix(
        IntPtr hwnd,
        string desc,
        bool webview,
        ref int writes,
        ref bool gaveUpLogged,
        ref long lastWriteMs,
        long nowMs)
    {
        uint tid = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (tid == 0)
        {
            return CheckResult.Retry;
        }

        IntPtr hkl = NativeMethods.GetKeyboardLayout(tid);
        if ((unchecked((uint)hkl.ToInt64()) & 0xFFFF) != LangIdZhCn)
        {
            return CheckResult.Skip;
        }

        IntPtr imeWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
        if (imeWnd == IntPtr.Zero)
        {
            return CheckResult.Retry;
        }

        if (!ImcSend(imeWnd, NativeMethods.IMC_GETCONVERSIONMODE, IntPtr.Zero, ReadTimeoutMs, out IntPtr read))
        {
            return CheckResult.Retry;
        }

        uint mode = unchecked((uint)read.ToInt64());
        if ((mode & ImeCmodeNative) != 0)
        {
            return CheckResult.Stable;
        }

        if (!webview)
        {
            if (writes >= MaxWritesPerWatch)
            {
                if (!gaveUpLogged)
                {
                    gaveUpLogged = true;
                    Logger.Warn($"English mode keeps coming back on {desc} - giving up for this focus session");
                }

                return CheckResult.Retry;
            }
        }
        else if (lastWriteMs >= 0 && nowMs - lastWriteMs < WebViewMinWriteIntervalMs)
        {
            return CheckResult.Retry;
        }

        writes++;
        lastWriteMs = nowMs;
        if (ImcSend(imeWnd, NativeMethods.IMC_SETCONVERSIONMODE, (IntPtr)ChineseConversionMode, WriteTimeoutMs, out _))
        {
            Logger.Info($"forced Chinese mode on {desc} (previous mode {mode}{(webview ? ", webview" : string.Empty)})");
            if (webview && writes % WebViewWarnEveryWrites == 0)
            {
                Logger.Warn($"webview keeps switching back to English mode: {desc} (writes so far {writes})");
            }
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            string hint = err == 5 /* ERROR_ACCESS_DENIED */
                ? " - target window may be running elevated"
                : string.Empty;
            Logger.Warn($"failed to set Chinese mode for {desc}, win32 error {err}{hint}");
        }

        return CheckResult.Retry;
    }

    private static bool ImcSend(IntPtr imeWnd, uint imc, IntPtr arg, int timeoutMs, out IntPtr result)
        => NativeMethods.SendMessageTimeoutW(
            imeWnd,
            NativeMethods.WM_IME_CONTROL,
            (IntPtr)(long)imc,
            arg,
            NativeMethods.SMTO_ABORTIFHUNG,
            (uint)timeoutMs,
            out result);

    private static string DescribeWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string proc = "?";
        try
        {
            proc = Process.GetProcessById(unchecked((int)pid)).ProcessName;
        }
        catch
        {
        }

        return $"hwnd=0x{hwnd.ToString("X")} pid={pid} proc={proc}";
    }
}
