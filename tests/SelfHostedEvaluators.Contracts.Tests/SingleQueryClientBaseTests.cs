using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Contracts.Tests;

public sealed class SingleQueryClientBaseTests
{
    private static readonly QueryWindow Window = new(
        DateTimeOffset.Parse("2026-09-30T12:00:00Z"), TimeSpan.FromMinutes(2));

    [Test]
    public async Task FanOutIsBoundedAndKeepsKeysAndWindow()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var started = 0;
        var counters = new object();
        await using var client = new DelegateClient(2, async (query, window, ct) =>
        {
            lock (counters)
            {
                active++;
                peak = Math.Max(peak, active);
            }
            if (Interlocked.Increment(ref started) == 2)
            {
                entered.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(ct);
                return new SignalResult.Observation(query.Key, window.Interval.TotalMinutes, null, window.ScheduledAt);
            }
            finally
            {
                lock (counters)
                {
                    active--;
                }
            }
        });
        var queries = Queries(6);
        var pending = client.QueryBatchAsync(queries, Window, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Volatile.Read(ref started), Is.EqualTo(2));
        }
        finally
        {
            release.TrySetResult();
        }
        var results = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(peak, Is.EqualTo(2));
            Assert.That(results.Select(result => result.Key), Is.EqualTo(queries.Select(query => query.Key)));
            Assert.That(results.Cast<SignalResult.Observation>().Select(result => result.Value), Is.All.EqualTo(2));
            Assert.That(results.Cast<SignalResult.Observation>().Select(result => result.ObservedAt),
                Is.All.EqualTo(Window.ScheduledAt));
        });
    }

    [Test]
    public async Task ExceptionsFailOnlyTheirQueryAndDoNotLeakExceptionText()
    {
        await using var client = new DelegateClient(2, (query, _, _) => query.Key.Name switch
        {
            "0" => throw new InvalidOperationException("credential=must-not-leak"),
            "1" => throw new TimeoutException("credential=must-not-leak"),
            "2" => throw new OperationCanceledException("independent transport cancellation"),
            _ => Task.FromResult<SignalResult>(new SignalResult.NoData(query.Key, "no samples"))
        });

        var results = await client.QueryBatchAsync(Queries(4), Window, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(((SignalResult.Failure)results[0]).Code, Is.EqualTo("query_failed"));
            Assert.That(((SignalResult.Failure)results[1]).Code, Is.EqualTo("timeout"));
            Assert.That(((SignalResult.Failure)results[2]).Code, Is.EqualTo("timeout"));
            Assert.That(results[3], Is.TypeOf<SignalResult.NoData>());
            Assert.That(results.OfType<SignalResult.Failure>().Select(result => result.Message),
                Has.None.Contains("credential"));
        });
    }

    [Test]
    public async Task ResultIdentityMismatchAndNullBecomeFailuresForTheRequestedKey()
    {
        await using var client = new DelegateClient(1, (query, _, _) =>
            Task.FromResult<SignalResult>(query.Key.Name == "0"
                ? new SignalResult.NoData(new SignalKey(new Uri("https://other.example/"), "other", "0"), "wrong")
                : null!));
        var queries = Queries(2);

        var results = await client.QueryBatchAsync(queries, Window, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results.Cast<SignalResult.Failure>().Select(result => result.Code),
                Is.All.EqualTo("invalid_result"));
            Assert.That(results.Select(result => result.Key), Is.EqualTo(queries.Select(query => query.Key)));
        });
    }

    [Test]
    public async Task ExplicitFailurePreservesRetryAfter()
    {
        await using var client = new DelegateClient(1, (query, _, _) => Task.FromResult<SignalResult>(
            new SignalResult.Failure(query.Key, "rate_limited", "retry later", TimeSpan.FromSeconds(30))));

        var result = (await client.QueryBatchAsync(Queries(1), Window, CancellationToken.None)).Single();

        Assert.That(((SignalResult.Failure)result).RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task PreCanceledBatchDoesNotStartQueries()
    {
        var calls = 0;
        await using var client = new DelegateClient(2, (query, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<SignalResult>(new SignalResult.NoData(query.Key, "unused"));
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await client.QueryBatchAsync(Queries(3), Window, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task CancellationStopsQueuedQueriesAndReachesTheActiveQuery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var client = new DelegateClient(1, async (query, _, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new SignalResult.NoData(query.Key, "unreachable");
            }
            finally
            {
                stopped.TrySetResult();
            }
        });
        using var cancellation = new CancellationTokenSource();
        var pending = client.QueryBatchAsync(Queries(3), Window, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)),
            Throws.InstanceOf<OperationCanceledException>());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task CancellationKeepsBatchObservableUntilIgnoredQueryFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SignalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var client = new DelegateClient(1, async (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                return await release.Task;
            }
            finally
            {
                exited.TrySetResult();
            }
        });
        using var cancellation = new CancellationTokenSource();
        var queries = Queries(4);
        var pending = client.QueryBatchAsync(queries, Window, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.That(async () => await pending.WaitAsync(TimeSpan.FromMilliseconds(100)),
                Throws.TypeOf<TimeoutException>(),
                "Cancellation must not complete the batch while a query is still running.");
            Assert.Multiple(() =>
            {
                Assert.That(pending.IsCompleted, Is.False);
                Assert.That(exited.Task.IsCompleted, Is.False);
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
            });
        }
        finally
        {
            release.TrySetResult(new SignalResult.NoData(queries[0].Key, "released"));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.Multiple(() =>
        {
            Assert.That(pending.IsCanceled, Is.True);
            Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task EmptyBatchStartsNoQueries()
    {
        await using var client = new DelegateClient(1, (_, _, _) =>
            throw new InvalidOperationException("Empty batches must not invoke the plugin."));

        var results = await client.QueryBatchAsync([], Window, CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveParallelismIsRejected(int parallelism)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegateClient(parallelism,
            (_, _, _) => throw new InvalidOperationException("Not invoked.")));
    }

    private static SignalQuery[] Queries(int count) => Enumerable.Range(0, count)
        .Select(index => new SignalQuery(new SignalKey(new Uri("https://model.example/"), "api", index.ToString()),
            JsonSerializer.SerializeToElement(new { }))).ToArray();

    private sealed class DelegateClient(int parallelism,
        Func<SignalQuery, QueryWindow, CancellationToken, Task<SignalResult>> query) : SingleQueryClientBase(parallelism)
    {
        protected override Task<SignalResult> QueryAsync(SignalQuery signal, QueryWindow window, CancellationToken ct) =>
            query(signal, window, ct);
    }
}
