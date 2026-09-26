using System.Collections.Concurrent;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Tests.Transcription;

public class TranscriptionSchedulerTests
{
    private static FinalUtterance Final(double seconds = 1) =>
        new(Guid.NewGuid(), new float[(int)(seconds * 16000)], TimeSpan.Zero, TimeSpan.FromSeconds(seconds));

    private static PartialSnapshot Partial(Guid id, int seq) => new(id, new float[8000], TimeSpan.Zero, seq);

    [Fact]
    public async Task Under_slowdown_partials_are_dropped_but_no_final_is_lost()
    {
        var fake = new FakeTranscriber(TimeSpan.FromMilliseconds(80));
        await using var scheduler = new TranscriptionScheduler();
        var finals = new ConcurrentBag<Guid>();
        var partials = new ConcurrentBag<int>();
        scheduler.FinalCompleted += (u, _) => finals.Add(u.UtteranceId);
        scheduler.PartialCompleted += (p, _) => partials.Add(p.Sequence);
        scheduler.Start(new TranscriberSet(fake));

        var expected = new List<Guid>();
        int seq = 0;
        for (int u = 0; u < 5; u++)
        {
            var id = Guid.NewGuid();
            for (int p = 0; p < 10; p++)
            {
                scheduler.OfferPartial(Partial(id, ++seq)); // arrive much faster than inference
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            var final = new FinalUtterance(id, new float[16000], TimeSpan.Zero, TimeSpan.FromSeconds(1));
            expected.Add(id);
            scheduler.EnqueueFinal(final);
        }

        await scheduler.WaitForFinalsAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        finals.Should().BeEquivalentTo(expected);
        scheduler.DroppedPartials.Should().BeGreaterThan(0);
        partials.Count.Should().BeLessThan(50);
    }

    [Fact]
    public async Task Finals_are_processed_in_order_and_before_partials()
    {
        var fake = new FakeTranscriber(TimeSpan.FromMilliseconds(30));
        await using var scheduler = new TranscriptionScheduler();
        var order = new ConcurrentQueue<string>();
        scheduler.FinalCompleted += (u, _) => order.Enqueue("F" + u.Duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        scheduler.PartialCompleted += (p, _) => order.Enqueue("P");

        var blocker = Final(0.5);
        scheduler.Start(new TranscriberSet(fake));
        scheduler.EnqueueFinal(blocker);
        await Task.Delay(5, TestContext.Current.CancellationToken);
        scheduler.OfferPartial(Partial(Guid.NewGuid(), 1));
        scheduler.EnqueueFinal(Final(1));
        scheduler.EnqueueFinal(Final(2));

        await scheduler.WaitForFinalsAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        order.Take(3).Should().Equal("F0.5", "F1", "F2");
    }

    [Fact]
    public async Task Partial_is_dropped_when_its_final_arrives()
    {
        var fake = new FakeTranscriber(TimeSpan.FromMilliseconds(50));
        await using var scheduler = new TranscriptionScheduler();
        int partials = 0;
        scheduler.PartialCompleted += (_, _) => Interlocked.Increment(ref partials);
        scheduler.Start(new TranscriberSet(fake));

        scheduler.EnqueueFinal(Final()); // keeps the worker busy
        var id = Guid.NewGuid();
        scheduler.OfferPartial(Partial(id, 1));
        scheduler.EnqueueFinal(new FinalUtterance(id, new float[16000], TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        await scheduler.WaitForFinalsAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        partials.Should().Be(0);
    }

    [Fact]
    public async Task Transient_failures_are_retried_and_the_final_is_not_lost()
    {
        var fake = new FakeTranscriber { FailOnCall = call => call == 1 ? new TranscriptionException("net down") { IsTransient = true, RetryAfter = TimeSpan.FromMilliseconds(50) } : null };
        await using var scheduler = new TranscriptionScheduler();
        var done = new TaskCompletionSource();
        var failures = 0;
        scheduler.JobFailed += (_, _, retry) => { retry.Should().BeTrue(); Interlocked.Increment(ref failures); };
        scheduler.FinalCompleted += (_, _) => done.TrySetResult();
        scheduler.Start(new TranscriberSet(fake));
        scheduler.EnqueueFinal(Final());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        failures.Should().Be(1);
    }

    [Fact]
    public async Task Minimum_partial_interval_is_respected()
    {
        var fake = new FakeTranscriber();
        await using var scheduler = new TranscriptionScheduler(new SchedulerOptions { MinPartialInterval = TimeSpan.FromMilliseconds(300) });
        var times = new ConcurrentBag<DateTime>();
        scheduler.PartialCompleted += (_, _) => times.Add(DateTime.UtcNow);
        scheduler.Start(new TranscriberSet(fake));
        var id = Guid.NewGuid();
        var offering = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 1; i <= 20; i++)
        {
            scheduler.OfferPartial(Partial(id, i));
            await Task.Delay(40, TestContext.Current.CancellationToken);
        }
        offering.Stop();
        await Task.Delay(350, TestContext.Current.CancellationToken);
        var sorted = times.OrderBy(t => t).ToList();
        // The loop takes ~0.8 s locally but up to twice that on a slow CI runner (coarse timers): the bound follows the
        // measured time (one partial at the start, one per 300 ms, one still pending when the loop ends), the spacing
        // below is what matters.
        int maxPartials = (int)(offering.Elapsed.TotalMilliseconds / 300) + 2;
        sorted.Count.Should().BeInRange(2, maxPartials);
        sorted.Count.Should().BeLessThan(20, "most offered partials are dropped");
        for (int i = 1; i < sorted.Count; i++)
        {
            (sorted[i] - sorted[i - 1]).TotalMilliseconds.Should().BeGreaterThan(280);
        }
    }

    [Fact]
    public async Task Swapping_transcribers_waits_for_running_inference()
    {
        var slow = new FakeTranscriber(TimeSpan.FromMilliseconds(200));
        var next = new FakeTranscriber();
        await using var scheduler = new TranscriptionScheduler();
        scheduler.Start(new TranscriberSet(slow));
        scheduler.EnqueueFinal(Final());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var old = await scheduler.SwapTranscribersAsync(new TranscriberSet(next));
        old!.Final.Should().BeSameAs(slow);
        slow.Calls.Should().HaveCount(1);
        scheduler.EnqueueFinal(Final());
        await scheduler.WaitForFinalsAsync(TestContext.Current.CancellationToken);
        next.Calls.Should().HaveCount(1);
    }
}
