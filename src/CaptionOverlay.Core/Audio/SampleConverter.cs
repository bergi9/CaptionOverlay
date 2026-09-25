using System.Buffers.Binary;
using NAudio.Wave;

namespace CaptionOverlay.Core.Audio;

/// <summary>Converts raw capture buffers (float32 or PCM 16/24/32-bit) to interleaved float samples.</summary>
public static class SampleConverter
{
    private static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat format) => format.Encoding switch
    {
        WaveFormatEncoding.IeeeFloat => true,
        WaveFormatEncoding.Extensible when format is WaveFormatExtensible ext => ext.SubFormat == KsDataFormatSubtypeIeeeFloat,
        _ => false,
    };

    public static bool IsPcm(WaveFormat format) => format.Encoding switch
    {
        WaveFormatEncoding.Pcm => true,
        WaveFormatEncoding.Extensible when format is WaveFormatExtensible ext => ext.SubFormat == KsDataFormatSubtypePcm,
        _ => false,
    };

    public static int SampleCount(int byteCount, WaveFormat format) => byteCount / (format.BitsPerSample / 8);

    public static float[] ToFloat(ReadOnlySpan<byte> data, WaveFormat format)
    {
        var result = new float[SampleCount(data.Length, format)];
        ToFloat(data, format, result);
        return result;
    }

    public static void ToFloat(ReadOnlySpan<byte> data, WaveFormat format, Span<float> destination)
    {
        int bits = format.BitsPerSample;
        int count = SampleCount(data.Length, format);
        if (destination.Length < count)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        if (IsFloat(format) && bits == 32)
        {
            for (int i = 0; i < count; i++)
            {
                destination[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4));
            }
            return;
        }

        if (!IsPcm(format))
        {
            throw new NotSupportedException($"Unsupported capture format: {format.Encoding}, {bits} bit.");
        }

        switch (bits)
        {
            case 16:
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2)) / 32768f;
                }
                break;
            case 24:
                for (int i = 0; i < count; i++)
                {
                    int o = i * 3;
                    int v = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                    destination[i] = v / 8388608f;
                }
                break;
            case 32:
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)) / 2147483648f;
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported PCM bit depth: {bits}.");
        }
    }
}
