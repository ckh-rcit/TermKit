using System;
using System.Runtime.InteropServices;

namespace TermKit.Services;

internal static class ErrorReporter
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    internal static void Report(string operation, Exception error, bool fatal = false)
    {
        try { Logger.Log($"{operation}: {error}", LogType.Error); } catch { }
        try
        {
            MessageBoxW(IntPtr.Zero, $"{operation}\n\n{error.Message}\n\n{(fatal ? "The application must close. " : "")}Applied changes are not rolled back. Review the log before retrying.\nLog: {Logger.LogPath}", "TermKit", 0x10);
        }
        catch { }
    }
}