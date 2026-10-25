using System;
using System.Threading;

namespace NoEnglishMode;

// "Local\" scopes the mutex to the current logon session.
internal static class SingleInstance
{
    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\NoEnglishMode.SingleInstance", out bool createdNew);
        return createdNew;
    }
}
