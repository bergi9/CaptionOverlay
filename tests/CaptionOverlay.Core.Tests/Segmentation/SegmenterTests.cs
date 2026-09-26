using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Vad;

namespace CaptionOverlay.Core.Tests.Segmentation;

public class UtteranceSegmenterTests
{
    private const int Frame = 512;
    private const double FrameMs = 32;

    private static List<SegmenterEvent> Feed(UtteranceSegmenter seg, params (double Ms, float Prob)[] parts)
    {
        var events = new List<SegmenterEvent>();
        foreach (var (ms, prob) in parts)
        {
            int frames = (int)Math.Round(ms / FrameMs);
            for (int i = 0; i < frames; i++)
            {
                var frame = new float[Frame];
                Array.Fill(frame, prob > 0.5f ? 0.1f : 0.001f);
                events.AddRange(seg.Process(frame, prob));
            }
        }
        return events;
    }

    [Fact]
    public void Speech_then_silence_emits_one_final_with_preroll()
    {
        var seg = new UtteranceSegmenter(new SegmenterOptions { EmitPartials = false });
        var events = Feed(seg, (1000, 0.0f), (2000, 0.9f), (1000, 0.0f));

        var final = events.OfType<FinalUtterance>().Should().ContainSingle().Subject;
        // Speech starts at 1.0 s; 300 ms pre-roll (frame-rounded).
        final.StartOffset.TotalMilliseconds.Should().BeInRange(1000 - 300 - FrameMs, 1000 - 300 + FrameMs);
        // 2 s speech + pre-roll + ≤ 200 ms post-roll.
        final.Duration.TotalMilliseconds.Should().BeInRange(2250, 2550);
        seg.State.Should().Be(SegmenterState.Idle);
    }

    [Fact]
    public void Final_is_emitted_after_end_silence()
    {
        var seg = new UtteranceSegmenter(new SegmenterOptions { EmitPartials = false, EndSilenceMs = 600 });
        Feed(seg, (1000, 0.9f), (500, 0.0f)).OfType<FinalUtterance>().Should().BeEmpty();
        Feed(seg, (200, 0.0f)).OfType<FinalUtterance>().Should().ContainSingle();
    }

    [Fact]
    public void Short_blips_do_not_start_an_utterance()
    {
        var seg = new UtteranceSegmenter();
        var events = Feed(seg, (160, 0.9f), (500, 0.0f), (160, 0.9f), (1000, 0.0f));
        events.Should().BeEmpty();
    }

    [Fact]
    public void Too_short_utterances_are_discarded()
    {
        var seg = new UtteranceSegmenter(new SegmenterOptions { MinSpeechMs = 250, MinUtteranceMs = 400 });
        var events = Feed(seg, (300, 0.9f), (1000, 0.0f));
        events.OfType<FinalUtterance>().Should().BeEmpty();
        events.OfType<UtteranceDiscarded>().Should().ContainSingle();
    }

    [Fact]
    public void Hysteresis_keeps_speaking_between_thresholds()
    {
        var seg = new UtteranceSegmenter(new SegmenterOptions { EmitPartials = false });
        var events = Feed(seg, (500, 0.9f), (1500, 0.4f), (500, 0.9f), (1000, 0.0f));
        events.OfType<FinalUtterance>().Should().ContainSingle()
            .Which.Duration.TotalMilliseconds.Should().BeGreaterThan(2400);
    }

    [Fact]
    public void Partials_are_emitted_every_interval_with_growing_audio()
    {
        var seg = new UtteranceSegmenter(new SegmenterOptions { PartialIntervalMs = 500 });
        var events = Feed(seg, (2600, 0.9f));
        var partials = events.OfType<PartialSnapshot>().ToList();
        partials.Count.Should().BeInRange(4, 6);
        partials.Select(p => p.Samples.Length).Should().BeInAscendingOrder();
        partials.Select(p => p.Sequence).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
        partials.Select(p => p.UtteranceId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void Long_speech_is_force_cut_at_max_length_without_losing_audio()
    {
        var seg = new UtteranceSegmenter(new SegmenterOptions { EmitPartials = false, MaxUtteranceSec = 12 });
        var events = Feed(seg, (30000, 0.9f), (1000, 0.0f));
        var finals = events.OfType<FinalUtterance>().ToList();
        finals.Count.Should().Be(3);
        finals.Should().OnlyContain(f => f.Duration.TotalSeconds <= 12.001);
        for (int i = 1; i < finals.Count; i++)
        {
            finals[i].StartOffset.Should().Be(finals[i - 1].End, "cuts must not drop audio");
        }
        finals.Select(f => f.UtteranceId).Should().OnlyHaveUniqueItems();
        finals.Select(f => f.IsForcedCut).Should().Equal([true, true, false], "the last one ends on silence, the others at the length limit");
    }

    [Fact]
    public void Forced_cut_prefers_the_quietest_window()
    {
        var seg = new UtteranceSegmenter(new SegmenterOptions { EmitPartials = false, MaxUtteranceSec = 12 });
        var events = new List<SegmenterEvent>();
        // Loud speech with a quiet dip at ~11 s (inside the 2 s search window before the 12 s cut).
        for (int i = 0; i < 12.5 * 16000 / Frame; i++)
        {
            double t = i * Frame / 16000.0;
            var frame = new float[Frame];
            Array.Fill(frame, t is > 10.9 and < 11.1 ? 0.0001f : 0.2f);
            events.AddRange(seg.Process(frame, 0.9f));
        }
        var final = events.OfType<FinalUtterance>().Should().ContainSingle().Subject;
        final.End.TotalSeconds.Should().BeInRange(10.85, 11.15);
    }
}

public class VadSegmentationFixtureTests
{
    private static List<FinalUtterance> Segment(string wav)
    {
        using var vad = new SileroVad(Fixtures.SileroModel);
        var seg = new UtteranceSegmenter(new SegmenterOptions { EmitPartials = false });
        var fe = new AudioFrontEnd(SileroVad.FrameSamples);
        var samples = WavIO.ReadMono16k(Fixtures.Path(wav));
        var finals = new List<FinalUtterance>();
        void OnFrame(float[] f) => finals.AddRange(seg.Process(f, vad.Process(f)).OfType<FinalUtterance>());
        fe.Push(new AudioChunk(samples, 16000, 1), OnFrame);
        // Trailing second of silence so the last utterance ends naturally.
        fe.Push(new AudioChunk(new float[16000], 16000, 1), OnFrame);
        fe.Flush(OnFrame);
        finals.AddRange(seg.Flush().OfType<FinalUtterance>());
        return finals;
    }

    [Fact]
    public void Silero_reports_speech_on_speech_and_not_on_silence()
    {
        using var vad = new SileroVad(Fixtures.SileroModel);
        var speech = WavIO.ReadMono16k(Fixtures.Path("speech_en.wav"));
        var probs = new List<float>();
        for (int i = 0; i + 512 <= speech.Length; i += 512)
        {
            probs.Add(vad.Process(speech.AsSpan(i, 512)));
        }
        // 1.5..3.5 s is solidly inside the first sentence.
        probs.Skip(47).Take(62).Average().Should().BeGreaterThan(0.7f);
        probs.Take(25).Max().Should().BeLessThan(0.3f, "the first second is silence");
    }

    [Fact]
    public void Speech_segments_match_labels_within_200ms()
    {
        var labels = Fixtures.Labels("speech_en.wav");
        var finals = Segment("speech_en.wav");
        finals.Should().HaveCount(labels.Count);
        foreach (var (label, final) in labels.Zip(finals))
        {
            // Utterance audio includes up to 300 ms pre-roll and 200 ms post-roll by design; the
            // detected speech boundaries are those minus the padding.
            double speechStart = final.StartOffset.TotalMilliseconds + 300;
            double speechEnd = final.End.TotalMilliseconds - 200;
            speechStart.Should().BeApproximately(label.StartMs, 200, $"start of \"{label.Text}\"");
            speechEnd.Should().BeApproximately(label.EndMs, 200, $"end of \"{label.Text}\"");
        }
    }

    [Fact]
    public void Silence_yields_no_utterances()
    {
        Segment("silence_10s.wav").Should().BeEmpty();
    }

    [Fact]
    public void Thirty_second_monologue_is_split_into_chunks_of_at_most_12s()
    {
        var finals = Segment("monologue_en_30s.wav");
        finals.Count.Should().BeGreaterThanOrEqualTo(3);
        finals.Should().OnlyContain(f => f.Duration.TotalSeconds <= 12.001);
        finals.Sum(f => f.Duration.TotalSeconds).Should().BeGreaterThan(28);
    }
}
