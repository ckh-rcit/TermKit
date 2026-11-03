using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public enum BatchItemStatus
    {
        NotLookedUp,
        Ready,
        NeedsAttention,
        Queued,
        Running,
        Succeeded,
        Failed,
        NotStarted
    }

    /// <summary>One termination in a batch. Property changes are raised on the context that created the item so UI bindings stay safe.</summary>
    public sealed class BatchItem : INotifyPropertyChanged
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;
        private BatchItemStatus _status = BatchItemStatus.NotLookedUp;
        private string _message = string.Empty;

        public BatchItem(string taskNumber) => TaskNumber = taskNumber;

        public string TaskNumber { get; }
        public string ShortDescription { get; set; } = string.Empty;
        public string TermUser { get; set; } = string.Empty;
        public string DelegateUser { get; set; } = string.Empty;
        public string EndDate { get; set; } = string.Empty;
        public bool NoRetention { get; set; }
        public string Summary { get; private set; } = string.Empty;

        public BatchItemStatus Status
        {
            get => _status;
            set { _status = value; Raise(); }
        }

        public string Message
        {
            get => _message;
            set { _message = value; Raise(); }
        }

        public string TermUserDisplay => string.IsNullOrWhiteSpace(TermUser) ? "(missing)" : TermUser;
        public string DelegateDisplay => !string.IsNullOrWhiteSpace(DelegateUser) ? DelegateUser : (NoRetention ? "(none)" : "(missing)");
        public string EndDateDisplay => string.IsNullOrWhiteSpace(EndDate) ? "(missing)" : EndDate;

        public string StatusText => Status switch
        {
            BatchItemStatus.NotLookedUp => "Not looked up",
            BatchItemStatus.Ready => "Ready",
            BatchItemStatus.NeedsAttention => "Needs attention",
            BatchItemStatus.Queued => "Queued",
            BatchItemStatus.Running => "Running",
            BatchItemStatus.Succeeded => "Done",
            BatchItemStatus.Failed => "Failed",
            _ => "Not started"
        };

        public RetentionConversionRequest ToRequest() => new()
        {
            TaskNumber = TaskNumber,
            TargetUserInput = TermUser,
            DelegateInput = DelegateUser,
            RetentionDateText = EndDate,
            NoRetention = NoRetention
        };

        public void SetSummary(string summary) => Summary = summary;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh() => Raise();

        private void Raise()
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            // An empty name refreshes every binding, including the computed display properties.
            var args = new PropertyChangedEventArgs(string.Empty);
            if (_context == null || SynchronizationContext.Current == _context) handler(this, args);
            else _context.Post(_ => handler(this, args), null);
        }
    }

    /// <summary>Builds a batch from pasted task numbers and ServiceNow lookups.</summary>
    public static class BatchPlanner
    {
        public const int MaxItems = 50;
        private static readonly Regex TicketPattern = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);

        public static IReadOnlyList<string> ParseTaskNumbers(string? text, out IReadOnlyList<string> rejected)
        {
            var accepted = new List<string>();
            var bad = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in Regex.Split(text ?? string.Empty, @"[\s,;]+"))
            {
                if (token.Length == 0) continue;
                if (!TicketPattern.IsMatch(token)) { bad.Add(token); continue; }
                if (seen.Add(token)) accepted.Add(token.ToUpperInvariant());
            }
            rejected = bad;
            return accepted;
        }

        /// <summary>Copies a lookup into the item and decides whether it is complete enough to process.</summary>
        public static void ApplyLookup(BatchItem item, ServiceNowLookupResult result, DateTime today)
        {
            if (!result.Success || result.Details == null)
            {
                item.Status = BatchItemStatus.NeedsAttention;
                item.Message = result.Message;
                return;
            }

            var details = result.Details;
            item.ShortDescription = details.ShortDescription;
            item.TermUser = details.TermUser ?? string.Empty;
            item.DelegateUser = details.DelegateUser ?? string.Empty;
            item.NoRetention = details.RetainEmail == false;
            item.EndDate = item.NoRetention
                ? RetentionRules.NoRetentionEndDate(today).ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)
                : details.RetentionEndDate?.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

            var problems = new List<string>(details.Warnings);
            if (item.TermUser.Length == 0 && !problems.Any(p => p.StartsWith("Term user", StringComparison.Ordinal))) problems.Add("Term user is missing.");
            item.Message = problems.Count > 0
                ? string.Join(" ", problems)
                : (item.NoRetention ? "No email retention requested; date set to tomorrow, delegate optional." : string.Empty);
            item.Status = problems.Count == 0 ? BatchItemStatus.Ready : BatchItemStatus.NeedsAttention;
            item.Refresh();
        }

        /// <summary>Two tasks for the same person would race each other, so only the first stays ready.</summary>
        public static void FlagDuplicates(IEnumerable<BatchItem> items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item.Status != BatchItemStatus.Ready) continue;
                if (seen.Add(item.TermUser)) continue;
                item.Status = BatchItemStatus.NeedsAttention;
                item.Message = $"{item.TermUser} is already in this batch.";
            }
        }
    }

    /// <summary>Runs ready items with a concurrency limit. One failure never stops the others.</summary>
    public static class BatchRunner
    {
        public static async Task RunAsync(IReadOnlyList<BatchItem> items, int maxConcurrency, Func<BatchItem, Task<RetentionConversionResult>> process, CancellationToken stopToken)
        {
            if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
            using var gate = new SemaphoreSlim(maxConcurrency);
            var ready = items.Where(item => item.Status == BatchItemStatus.Ready).ToList();
            foreach (var item in ready) item.Status = BatchItemStatus.Queued;

            await Task.WhenAll(ready.Select(item => RunOneAsync(item, gate, process, stopToken)));
        }

        private static async Task RunOneAsync(BatchItem item, SemaphoreSlim gate, Func<BatchItem, Task<RetentionConversionResult>> process, CancellationToken stopToken)
        {
            try
            {
                await gate.WaitAsync(stopToken);
            }
            catch (OperationCanceledException)
            {
                item.Status = BatchItemStatus.NotStarted;
                item.Message = "Stopped before this termination started.";
                return;
            }

            try
            {
                item.Status = BatchItemStatus.Running;
                item.Message = string.Empty;
                var result = await process(item);
                item.SetSummary(result.SummaryText);
                item.Status = result.Success ? BatchItemStatus.Succeeded : BatchItemStatus.Failed;
                item.Message = result.Success ? "Completed." : "The conversion did not complete.";
            }
            catch (Exception exception)
            {
                item.Status = BatchItemStatus.Failed;
                var firstLine = (exception.Message ?? string.Empty).Split('\n')[0].Trim();
                item.Message = firstLine.Length > 300 ? firstLine[..300] : firstLine;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
