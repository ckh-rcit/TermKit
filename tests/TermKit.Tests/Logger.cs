namespace TermKit.Services;

public enum LogType { Info, Success, Error, Warning, Summary, Ticket }

public static class Logger
{
    public static void Log(string message, LogType type = LogType.Info) { }
    public static IDisposable BeginScope(string name) => new Scope();
    private sealed class Scope : IDisposable { public void Dispose() { } }
}