using System;
using System.Reflection;

namespace NoEnglishMode;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // all files are written next to the exe, so on the first launch in a
        // folder require that no other files sit beside it
        if (AppFolder.IsFirstLaunchWithForeignFiles())
        {
            _ = NativeMethods.MessageBoxW(
                IntPtr.Zero,
                "本程序会把它的文件(日志等)写在自身所在文件夹,必须在专用文件夹中运行。\n\n"
                    + "检测到 no_english_mode.exe 所在文件夹中还有其他文件。"
                    + "请把它移到一个只存放本程序的文件夹中,然后再运行。",
                "NoEnglishMode",
                NativeMethods.MbOk | NativeMethods.MbIconInformation);
            return 1;
        }

        Logger.Init(AppFolder.ExeDirectory);
        Logger.Info($"starting no_english_mode v{Assembly.GetExecutingAssembly().GetName().Version}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error("unhandled exception", e.ExceptionObject as Exception);

        if (!SingleInstance.TryAcquire())
        {
            Logger.Info("another instance is already running - exiting");
            return 0;
        }

        try
        {
            return TrayApp.Run();
        }
        catch (Exception ex)
        {
            Logger.Error("fatal error", ex);
            return 1;
        }
    }
}
