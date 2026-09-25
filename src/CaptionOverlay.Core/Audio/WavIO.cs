using NAudio.Wave;

namespace CaptionOverlay.Core.Audio;

public static class WavIO
{
    /// <summary>Reads any WAV file NAudio understands and returns 16 kHz mono float samples.</summary>
    public static float[] ReadMono16k(string path)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        var bytes = new byte[reader.Length];
        int read = reader.Read(bytes, 0, bytes.Length);
        float[] interleaved = SampleConverter.ToFloat(bytes.AsSpan(0, read), format);
        return new Resampler().Process(interleaved, format.SampleRate, format.Channels);
    }

    /// <summary>Encodes 16 kHz mono float samples as a PCM16 WAV file in memory.</summary>
    public static byte[] EncodePcm16(ReadOnlySpan<float> samples, int sampleRate = Resampler.TargetSampleRate)
    {
        using var ms = new MemoryStream(44 + samples.Length * 2);
        WritePcm16(ms, samples, sampleRate);
        return ms.ToArray();
    }

    public static void WritePcm16(string path, ReadOnlySpan<float> samples, int sampleRate = Resampler.TargetSampleRate)
    {
        using var fs = File.Create(path);
        WritePcm16(fs, samples, sampleRate);
    }

    public static void WritePcm16(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        int dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (float s in samples)
        {
            writer.Write((short)Math.Clamp(MathF.Round(s * 32767f), -32768f, 32767f));
        }
    }
}
