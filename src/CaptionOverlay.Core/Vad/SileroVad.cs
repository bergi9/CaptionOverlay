using Microsoft.ML.OnnxRuntime;

namespace CaptionOverlay.Core.Vad;

/// <summary>
/// Silero VAD (v5/v6 ONNX layout: inputs <c>input</c>, <c>state</c>, <c>sr</c>; outputs <c>output</c>, <c>stateN</c>).
/// Processes 512-sample (32 ms) frames at 16 kHz. Like the reference implementation, the last
/// 64 samples of the previous frame are prepended as context.
/// </summary>
public sealed class SileroVad : IVoiceActivityDetector
{
    public const int FrameSamples = 512;
    public const int SampleRate = 16000;
    private const int ContextSamples = 64;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _stateName;
    private readonly string _srName;
    private readonly string _outputName;
    private readonly string _stateOutName;
    private readonly int[] _stateShape;
    private readonly float[] _input = new float[ContextSamples + FrameSamples];
    private readonly RunOptions _runOptions = new();
    private float[] _state;

    public SileroVad(string modelPath)
    {
        var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
        options.Dispose();

        var inputs = _session.InputMetadata;
        _inputName = Find(inputs.Keys, "input");
        _stateName = Find(inputs.Keys, "state");
        _srName = Find(inputs.Keys, "sr");
        var outputs = _session.OutputMetadata.Keys.ToList();
        _outputName = Find(outputs, "output");
        _stateOutName = outputs.First(n => n != _outputName);

        // State is [2, batch, 128]; dynamic dims are reported as -1.
        _stateShape = inputs[_stateName].Dimensions.Select(d => d <= 0 ? 1 : d).ToArray();
        _state = new float[_stateShape.Aggregate(1, (a, b) => a * b)];
    }

    /// <summary>Default location next to the executable (copied from Assets at build time).</summary>
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");

    public int FrameSize => FrameSamples;

    public float Process(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException($"Silero VAD expects {FrameSamples}-sample frames.", nameof(frame));
        }

        frame.CopyTo(_input.AsSpan(ContextSamples));

        using var inputTensor = OrtValue.CreateTensorValueFromMemory(_input, [1, _input.Length]);
        using var stateTensor = OrtValue.CreateTensorValueFromMemory(_state, _stateShape.Select(d => (long)d).ToArray());
        using var srTensor = OrtValue.CreateTensorValueFromMemory(new[] { (long)SampleRate }, []);

        var names = new[] { _inputName, _stateName, _srName };
        var values = new[] { inputTensor, stateTensor, srTensor };
        using var results = _session.Run(_runOptions, names, values, [_outputName, _stateOutName]);

        float probability = results[0].GetTensorDataAsSpan<float>()[0];
        _state = results[1].GetTensorDataAsSpan<float>().ToArray();

        // Keep the tail of this frame as context for the next one.
        frame[^ContextSamples..].CopyTo(_input);
        return probability;
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_input);
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _session.Dispose();
    }

    private static string Find(IEnumerable<string> names, string preferred)
    {
        var list = names.ToList();
        return list.FirstOrDefault(n => n == preferred)
            ?? list.FirstOrDefault(n => n.StartsWith(preferred, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Silero VAD model has no '{preferred}' tensor (found: {string.Join(", ", list)}). Unsupported model version.");
    }
}
