using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace TermKit.Services
{
    public enum LogType
    {
        Info,
        Success,
        Error,
        Warning,
        Summary,
        Ticket
    }

    /// <summary>
    /// Central output log, ported from the WinForms script's Write-OutputLog / Write-SummaryLog /
    /// Copy-TicketNotesToClipboard functions. Appends timestamped, color-coded lines to a RichEditBox
    /// (or any host that consumes <see cref="LogEntryWritten"/>) and tracks the most recent
    /// "For IT Ticket" section so it can be copied to the clipboard on demand.
    /// </summary>
    public static class Logger
    {
        public static event Action<string, LogType>? LogEntryWritten;

        private static readonly List<string> _rawLines = new();
        private static readonly List<(string Line, LogType Type)> _history = new();
        private static readonly object _lock = new();
        public static string LogPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermKit", "Logs", $"{DateTime.Now:yyyy-MM-dd}.log");

        private static readonly System.Threading.AsyncLocal<string?> CurrentScope = new();

        /// <summary>Prefixes every line logged by this logical flow (and the tasks it awaits) until the scope is disposed.</summary>
        public static IDisposable BeginScope(string name)
        {
            var previous = CurrentScope.Value;
            CurrentScope.Value = name;
            return new ScopeToken(previous);
        }

        private sealed class ScopeToken : IDisposable
        {
            private readonly string? _previous;
            public ScopeToken(string? previous) => _previous = previous;
            public void Dispose() => CurrentScope.Value = _previous;
        }

        public static void Log(string message, LogType type = LogType.Info)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var scope = CurrentScope.Value;
            var line = scope == null ? $"[{timestamp}] {message}" : $"[{timestamp}] [{scope}] {message}";

            lock (_lock)
            {
                _rawLines.Add(line);
                _history.Add((line, type));
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                    System.IO.File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                catch { }
            }

            var handlers = LogEntryWritten;
            if (handlers == null) return;
            foreach (Action<string, LogType> handler in handlers.GetInvocationList())
            {
                try { handler(line, type); } catch { }
            }
        }

        /// <summary>
        /// Returns a snapshot of every logged line and its <see cref="LogType"/>, in order, so a UI
        /// can re-render the full log (e.g. after toggling a "show warnings" filter).
        /// </summary>
        public static IReadOnlyList<(string Line, LogType Type)> GetHistory()
        {
            lock (_lock)
            {
                return _history.ToList();
            }
        }

        public static Color GetColorForType(LogType type) => type switch
        {
            LogType.Success => Colors.LimeGreen,
            LogType.Error => Colors.Red,
            LogType.Warning => Colors.Orange,
            LogType.Summary => Colors.Cyan,
            LogType.Ticket => Colors.LightSkyBlue,
            _ => Colors.White
        };

        /// <summary>
        /// Extracts the notes from the most recent "--- For IT Ticket ---" / "---------------------"
        /// section in the log, matching Copy-TicketNotesToClipboard's regex-based parsing.
        /// </summary>
        public static IReadOnlyList<string> GetLastTicketNotes()
        {
            lock (_lock)
            {
                int startIndex = -1;
                int endIndex = -1;

                for (int i = _rawLines.Count - 1; i >= 0; i--)
                {
                    if (_rawLines[i].Contains("--- For IT Ticket ---"))
                    {
                        startIndex = i;
                        break;
                    }
                }

                if (startIndex == -1)
                {
                    return Array.Empty<string>();
                }

                for (int i = startIndex + 1; i < _rawLines.Count; i++)
                {
                    if (_rawLines[i].Contains("---------------------"))
                    {
                        endIndex = i;
                        break;
                    }
                }

                if (endIndex == -1)
                {
                    endIndex = _rawLines.Count;
                }

                var notes = new List<string>();
                var timestampPattern = new Regex(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]\s*(.*)");
                for (int i = startIndex + 1; i < endIndex; i++)
                {
                    var match = timestampPattern.Match(_rawLines[i]);
                    if (match.Success)
                    {
                        notes.Add(match.Groups[1].Value);
                    }
                }

                return notes;
            }
        }

        /// <summary>
        /// Copies the most recent ticket notes section to the clipboard. Returns the number of
        /// lines copied, or 0 if no ticket section / no notes were found.
        /// </summary>
        public static int CopyLastTicketNotesToClipboard()
        {
            var notes = GetLastTicketNotes();
            if (notes.Count == 0)
            {
                return 0;
            }

            var text = string.Join(Environment.NewLine, notes);
            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);
            return notes.Count;
        }

        /// <summary>
        /// Produces the "For IT Ticket" section lines for a batch of results and the running
        /// batch summary line, mirroring Write-SummaryLog. Results is keyed by user/item identity;
        /// each value is either a Dictionary&lt;string, List&lt;string&gt;&gt; of target->status details,
        /// or a flat List&lt;string&gt; of status items.
        /// </summary>
        public static List<string> WriteSummary(string actionType, IDictionary<string, object> results)
        {
            if (results.Count == 0) return new List<string>();

            var ticketLines = new List<string>();
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var informational = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var failurePattern = new Regex("Error|Verify Failed|User Not Found|Group Not Found|ID Error|Not Connected|Connection Required", RegexOptions.IgnoreCase);
            var informationalPattern = new Regex("Already Exists|Not Found|Skipped", RegexOptions.IgnoreCase);

            foreach (var userKey in results.Keys)
            {
                processed.Add(userKey);
                var userStatuses = new List<string>();

                if (results[userKey] is IDictionary<string, List<string>> targetPermissions)
                {
                    foreach (var targetKey in targetPermissions.Keys)
                    {
                        var detailItems = targetPermissions[targetKey].Distinct().ToList();
                        var details = string.Join(", ", detailItems);
                        if (!string.IsNullOrWhiteSpace(details))
                        {
                            ticketLines.Add($"ACTION: {actionType} | TARGET: {targetKey} | USER: {userKey} | STATUS: {details}");
                            userStatuses.AddRange(detailItems);
                        }
                    }
                }
                else if (results[userKey] is IEnumerable<string> statusItemsRaw)
                {
                    var statusItems = statusItemsRaw.Distinct().ToList();
                    var status = string.Join(", ", statusItems);
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        ticketLines.Add($"ACTION: {actionType} | ITEM: {userKey} | STATUS: {status}");
                        userStatuses.AddRange(statusItems);
                    }
                }

                bool userHadError = userStatuses.Any(s => failurePattern.IsMatch(s));
                bool userHadInfo = !userHadError && userStatuses.Any(s => informationalPattern.IsMatch(s));

                if (userHadError) failed.Add(userKey);
                else if (userHadInfo) informational.Add(userKey);
            }

            if (ticketLines.Count > 0)
            {
                Log("--- For IT Ticket ---", LogType.Ticket);
                foreach (var line in ticketLines) Log(line, LogType.Ticket);
                Log("---------------------", LogType.Ticket);
                Log("INFO: Ticket notes are ready. Use 'Copy Last Ticket Notes' to copy the most recent ticket section.", LogType.Info);
            }

            int totalProcessed = processed.Count;
            int totalInformational = informational.Count;
            int totalFailed = failed.Count;
            int totalSuccess = totalProcessed - totalInformational - totalFailed;

            Log($"Batch Summary for '{actionType}': Processed {totalProcessed} users. Successful: {totalSuccess}, Completed with Info/No Change: {totalInformational}, Failed: {totalFailed}.", LogType.Summary);

            return ticketLines;
        }

        public static string GetTicketLinesText(IEnumerable<string> ticketLines) => string.Join(Environment.NewLine, ticketLines);
    }
}
