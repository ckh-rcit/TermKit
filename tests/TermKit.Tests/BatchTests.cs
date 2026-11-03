using TermKit.Services;
using Xunit;

namespace TermKit.Tests;

public sealed class BatchTests
{
    private static BatchItem Ready(string task, string user = "") => new(task)
    {
        TermUser = string.IsNullOrEmpty(user) ? task + "@contoso.org" : user,
        DelegateUser = "delegate@contoso.org",
        EndDate = "12/31/2026",
        Status = BatchItemStatus.Ready
    };

    private static RetentionConversionResult Done() => new() { Success = true, SummaryText = "ok" };

    [Fact]
    public void TaskNumbersAreSplitNormalizedAndDeduplicated()
    {
        var tasks = BatchPlanner.ParseTaskNumbers("task1\r\nTASK2, task1;  TASK3\n\n", out var rejected);
        Assert.Equal(new[] { "TASK1", "TASK2", "TASK3" }, tasks);
        Assert.Empty(rejected);
    }

    [Fact]
    public void UnsafeTokensAreRejectedNotSent()
    {
        var tasks = BatchPlanner.ParseTaskNumbers("TASK1 TASK2^ORnumber=TASK3 TASK4&x", out var rejected);
        Assert.Equal(new[] { "TASK1" }, tasks);
        Assert.Equal(2, rejected.Count);
    }

    private static ServiceNowLookupResult Lookup(Action<ServiceNowTaskDetails> shape)
    {
        var details = new ServiceNowTaskDetails { TicketNumber = "TASK1", ShortDescription = "Termination" };
        shape(details);
        return new ServiceNowLookupResult { Success = true, Details = details };
    }

    [Fact]
    public void CompleteRetentionLookupIsReady()
    {
        var item = new BatchItem("TASK1");
        BatchPlanner.ApplyLookup(item, Lookup(d => { d.TermUser = "a@contoso.org"; d.DelegateUser = "b@contoso.org"; d.RetentionEndDate = new DateTime(2026, 12, 31); d.RetainEmail = true; }), new DateTime(2026, 11, 2));
        Assert.Equal(BatchItemStatus.Ready, item.Status);
        Assert.Equal("12/31/2026", item.EndDate);
        Assert.False(item.NoRetention);
    }

    [Fact]
    public void NoRetentionLookupIsReadyWithTomorrowAndNoDelegate()
    {
        var item = new BatchItem("TASK1");
        BatchPlanner.ApplyLookup(item, Lookup(d => { d.TermUser = "a@contoso.org"; d.RetainEmail = false; }), new DateTime(2026, 11, 2));
        Assert.Equal(BatchItemStatus.Ready, item.Status);
        Assert.True(item.NoRetention);
        Assert.Equal("11/03/2026", item.EndDate);
        Assert.Equal("(none)", item.DelegateDisplay);
    }

    [Fact]
    public void IncompleteOrFailedLookupsNeedAttention()
    {
        var missing = new BatchItem("TASK1");
        BatchPlanner.ApplyLookup(missing, Lookup(d => { d.TermUser = "a@contoso.org"; d.Warnings.Add("Delegate was not found."); }), DateTime.Today);
        Assert.Equal(BatchItemStatus.NeedsAttention, missing.Status);
        Assert.Contains("Delegate", missing.Message);

        var failed = new BatchItem("TASK2");
        BatchPlanner.ApplyLookup(failed, new ServiceNowLookupResult { Success = false, Message = "not found" }, DateTime.Today);
        Assert.Equal(BatchItemStatus.NeedsAttention, failed.Status);
        Assert.Equal("not found", failed.Message);

        var noUser = new BatchItem("TASK3");
        BatchPlanner.ApplyLookup(noUser, Lookup(d => { d.RetainEmail = false; }), DateTime.Today);
        Assert.Equal(BatchItemStatus.NeedsAttention, noUser.Status);
    }

    [Fact]
    public void SecondTaskForTheSamePersonIsHeldBack()
    {
        var first = Ready("TASK1", "Same@contoso.org");
        var second = Ready("TASK2", "same@CONTOSO.org");
        var other = Ready("TASK3");
        BatchPlanner.FlagDuplicates(new[] { first, second, other });
        Assert.Equal(BatchItemStatus.Ready, first.Status);
        Assert.Equal(BatchItemStatus.NeedsAttention, second.Status);
        Assert.Equal(BatchItemStatus.Ready, other.Status);
    }

    [Fact]
    public async Task RunnerNeverExceedsTheConcurrencyLimit()
    {
        var items = Enumerable.Range(1, 8).Select(i => Ready("TASK" + i)).ToList();
        var running = 0;
        var peak = 0;
        await BatchRunner.RunAsync(items, 3, async _ =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(40);
            Interlocked.Decrement(ref running);
            return Done();
        }, CancellationToken.None);

        Assert.Equal(3, peak);
        Assert.All(items, item => Assert.Equal(BatchItemStatus.Succeeded, item.Status));
    }

    [Fact]
    public async Task OneFailureNeverStopsTheOthersAndKeepsTheReason()
    {
        var items = new[] { Ready("TASK1"), Ready("TASK2"), Ready("TASK3") };
        await BatchRunner.RunAsync(items, 2, item => item.TaskNumber == "TASK2"
            ? throw new InvalidOperationException("Mailbox type instability detected.\nsecond line")
            : Task.FromResult(Done()), CancellationToken.None);

        Assert.Equal(BatchItemStatus.Succeeded, items[0].Status);
        Assert.Equal(BatchItemStatus.Failed, items[1].Status);
        Assert.Equal("Mailbox type instability detected.", items[1].Message);
        Assert.Equal(BatchItemStatus.Succeeded, items[2].Status);
    }

    [Fact]
    public async Task ResultThatReportsFailureIsFailedNotDone()
    {
        var item = Ready("TASK1");
        await BatchRunner.RunAsync(new[] { item }, 1, _ => Task.FromResult(new RetentionConversionResult { Success = false }), CancellationToken.None);
        Assert.Equal(BatchItemStatus.Failed, item.Status);
    }

    [Fact]
    public async Task StoppingLeavesQueuedItemsUnstartedAndRunningOnesFinish()
    {
        var items = Enumerable.Range(1, 4).Select(i => Ready("TASK" + i)).ToList();
        using var stop = new CancellationTokenSource();
        await BatchRunner.RunAsync(items, 1, async item =>
        {
            if (item.TaskNumber == "TASK1") stop.Cancel();
            await Task.Delay(20);
            return Done();
        }, stop.Token);

        Assert.Equal(BatchItemStatus.Succeeded, items[0].Status);
        Assert.All(items.Skip(1), item => Assert.Equal(BatchItemStatus.NotStarted, item.Status));
    }

    [Fact]
    public async Task ItemsThatAreNotReadyAreNeverProcessed()
    {
        var needsAttention = new BatchItem("TASK1") { Status = BatchItemStatus.NeedsAttention };
        var ready = Ready("TASK2");
        var processed = new List<string>();
        await BatchRunner.RunAsync(new[] { needsAttention, ready }, 2, item => { processed.Add(item.TaskNumber); return Task.FromResult(Done()); }, CancellationToken.None);
        Assert.Equal(new[] { "TASK2" }, processed);
        Assert.Equal(BatchItemStatus.NeedsAttention, needsAttention.Status);
    }

    [Fact]
    public void ItemBuildsTheSameRequestAsSingleMode()
    {
        var item = Ready("TASK1", "a@contoso.org");
        item.NoRetention = true;
        var request = item.ToRequest();
        Assert.Equal("TASK1", request.TaskNumber);
        Assert.Equal("a@contoso.org", request.TargetUserInput);
        Assert.Equal("delegate@contoso.org", request.DelegateInput);
        Assert.Equal("12/31/2026", request.RetentionDateText);
        Assert.True(request.NoRetention);
    }
}
