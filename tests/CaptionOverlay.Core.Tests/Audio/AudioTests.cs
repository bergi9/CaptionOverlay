using CaptionOverlay.Core.Audio;
using NAudio.Wave;

namespace CaptionOverlay.Core.Tests.Audio;

public class ResamplerTests
{
    [Fact]
    public void Sine_1kHz_48k_stereo_becomes_16k_mono_with_same_frequency_and_amplitude()
    {
        var input = Signals.Sine(1000, 48000, 2.0, channels: 2, amplitude: 0.5f);
        var resampler = new Resampler();

        // Feed in 10 ms chunks like WASAPI does.
        var output = new List<float>();
        int chunk = 480 * 2;
        for (int i = 0; i < input.Length; i += chunk)
        {
            output.AddRange(resampler.Process(input.AsSpan(i, Math.Min(chunk, input.Length - i)), 48000, 2));
        }

        output.Count.Should().BeCloseTo(32000, 200);

        // Skip filter warm-up, then measure over 1 s.
        var steady = output.Skip(4000).Take(16000).ToArray();
        int crossings = 0;
        for (int i = 1; i < steady.Length; i++)
        {
            if (steady[i - 1] < 0 && steady[i] >= 0)
            {
                crossings++;
            }
        }
        crossings.Should().BeInRange(998, 1002, "a 1 kHz tone has 1000 rising zero crossings per second");

        float peak = steady.Max(Math.Abs);
        peak.Should().BeInRange(0.47f, 0.53f);

        double rms = Math.Sqrt(steady.Average(s => s * s));
        rms.Should().BeApproximately(0.5 / Math.Sqrt(2), 0.02);
    }

    [Fact]
    public void Chunk_boundaries_do_not_click()
    {
        var input = Signals.Sine(440, 48000, 1.0, channels: 2);
        var resampler = new Resampler();
        var output = new List<float>();
        var rng = new Random(1);
        int pos = 0;
        while (pos < input.Length)
        {
            int size = Math.Min(input.Length - pos, 2 * rng.Next(1, 700));
            output.AddRange(resampler.Process(input.AsSpan(pos, size), 48000, 2));
            pos += size;
        }

        // Max step of a 440 Hz sine with amplitude 0.5 at 16 kHz is 2π·440/16000·0.5 ≈ 0.087.
        float maxStep = 0;
        for (int i = 2000; i < output.Count; i++)
        {
            maxStep = Math.Max(maxStep, Math.Abs(output[i] - output[i - 1]));
        }
        maxStep.Should().BeLessThan(0.1f);
    }

    [Fact]
    public void Passthrough_at_16k_downmixes_only()
    {
        var resampler = new Resampler();
        var result = resampler.Process([0.2f, 0.4f, -0.2f, -0.4f], 16000, 2);
        result.Should().Equal(new[] { 0.3f, -0.3f }, (a, b) => Math.Abs(a - b) < 1e-6);
    }
}

public class SampleConverterTests
{
    [Fact]
    public void Converts_pcm16()
    {
        byte[] data = [0x00, 0x40, 0x00, 0xC0]; // 16384, -16384
        SampleConverter.ToFloat(data, new WaveFormat(48000, 16, 1)).Should().Equal(0.5f, -0.5f);
    }

    [Fact]
    public void Converts_pcm24()
    {
        byte[] data = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0];
        SampleConverter.ToFloat(data, new WaveFormat(48000, 24, 1)).Should().Equal(0.5f, -0.5f);
    }

    [Fact]
    public void Converts_float32_including_extensible()
    {
        byte[] data = [.. BitConverter.GetBytes(0.25f), .. BitConverter.GetBytes(-1f)];
        SampleConverter.ToFloat(data, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)).Should().Equal(0.25f, -1f);
        SampleConverter.ToFloat(data, new WaveFormatExtensible(48000, 32, 1)).Should().Equal(0.25f, -1f);
    }
}

public class AudioFrontEndTests
{
    [Fact]
    public void Emits_fixed_size_frames_across_chunks()
    {
        var fe = new AudioFrontEnd(512);
        var frames = new List<float[]>();
        fe.Push(new AudioChunk(new float[300], 16000, 1), frames.Add);
        fe.Push(new AudioChunk(new float[800], 16000, 1), frames.Add);
        frames.Should().HaveCount(2);
        frames.Should().OnlyContain(f => f.Length == 512);
        fe.SamplesEmitted.Should().Be(1024);
    }

    [Fact]
    public void Injects_silence_when_timeline_lags_wall_clock()
    {
        var fe = new AudioFrontEnd(512, TimeSpan.FromMilliseconds(100));
        var frames = new List<float[]>();
        fe.StartClock(TimeSpan.Zero);
        fe.Push(new AudioChunk(new float[1600], 16000, 1), frames.Add); // 100 ms real audio

        fe.InjectSilenceIfIdle(TimeSpan.FromMilliseconds(150), frames.Add).Should().Be(0, "within tolerance");

        int injected = fe.InjectSilenceIfIdle(TimeSpan.FromSeconds(1.1), frames.Add);
        injected.Should().Be(16000 - 1600, "timeline catches up to wall clock minus tolerance");
        fe.Position.TotalSeconds.Should().BeApproximately(1.0, 0.04);
    }

    [Fact]
    public void Wav_roundtrip_is_16k_mono()
    {
        string path = System.IO.Path.GetTempFileName();
        try
        {
            var sine = Signals.Sine(300, 16000, 0.5);
            WavIO.WritePcm16(path, sine);
            var back = WavIO.ReadMono16k(path);
            back.Length.Should().Be(sine.Length);
            back.Zip(sine).Should().OnlyContain(p => Math.Abs(p.First - p.Second) < 1e-3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
