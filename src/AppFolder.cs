using System;
using System.IO;

namespace NoEnglishMode;

// Everything the app produces (the log) is written next to the exe, so the
// exe must live in a folder dedicated to it. On the first launch in a folder
// (no log.txt yet) any file that is not the program's own sitting next to the
// exe means the folder is not dedicated and startup is refused. Later
// launches are allowed: the existing log.txt marks the folder as adopted.
internal static class AppFolder
{
    public static string ExePath => typeof(AppFolder).Assembly.Location;

    public static string ExeDirectory
    {
        get
        {
            string? dir = Path.GetDirectoryName(ExePath);
            return string.IsNullOrEmpty(dir) ? "." : dir;
        }
    }

    public static bool IsFirstLaunchWithForeignFiles()
    {
        try
        {
            if (File.Exists(Path.Combine(ExeDirectory, Logger.LogFileName)))
            {
                return false;
            }

            string exeName = Path.GetFileName(ExePath);
            string ownPrefix = Path.GetFileNameWithoutExtension(ExePath) + ".";
            foreach (string entry in Directory.EnumerateFiles(ExeDirectory))
            {
                string name = Path.GetFileName(entry);
                if (string.Equals(name, exeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, Logger.LogFileName, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }
}
