using System.Text.Json.Serialization;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool FirstRunCompleted { get; set; }

    public AudioSettings Audio { get; set; } = new();

    public EngineSettings Engine { get; set; } = new();

    public ApiSettings Api { get; set; } = new();

    public OverlaySettings Overlay { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    public TranscriptSettings Transcripts { get; set; } = new();

    public GeneralSettings General { get; set; } = new();
}

public sealed class AudioSettings
{
    /// <summary>Output device id, or null to follow the system default.</summary>
    public string? DeviceId { get; set; }

    public bool UseVad { get; set; } = true;

    public float SpeechThreshold { get; set; } = 0.5f;

    public float SilenceThreshold { get; set; } = 0.35f;

    public int EndSilenceMs { get; set; } = 600;

    public double MaxUtteranceSec { get; set; } = 12;
}

[JsonConverter(typeof(JsonStringEnumConverter<EngineMode>))]
public enum EngineMode
{
    Local,
    Api,
}

public sealed class EngineSettings
{
    public EngineMode Mode { get; set; } = EngineMode.Local;

    public string? ModelId { get; set; }

    /// <summary>"auto" or an ISO 639-1 code.</summary>
    public string Language { get; set; } = "auto";

    public bool EnablePartials { get; set; } = true;

    public int PartialIntervalMs { get; set; } = 500;

    /// <summary>Optional separate (smaller) model for partial passes.</summary>
    public string? PartialModelId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<GpuPreference>))]
    public GpuPreference Gpu { get; set; } = GpuPreference.Auto;
}

public sealed class ApiSettings
{
    public string Provider { get; set; } = ApiProviderPreset.Groq.Id;

    public string BaseUrl { get; set; } = ApiProviderPreset.Groq.BaseUrl;

    public string Model { get; set; } = ApiProviderPreset.Groq.DefaultModel;

    public bool EnablePartials { get; set; }

    // The API key is deliberately not here: it lives DPAPI-encrypted in secrets.dat (SecretStore).
}

public sealed class OverlaySettings
{
    public string FontFamily { get; set; } = "Segoe UI";

    public double FontSize { get; set; } = 28;

    public int FontWeight { get; set; } = 600;

    public string TextColor { get; set; } = "#FFFFFFFF";

    public string OutlineColor { get; set; } = "#FF000000";

    public double OutlineThickness { get; set; } = 2;

    public string BackgroundColor { get; set; } = "#000000";

    public double BackgroundOpacity { get; set; } = 0.55;

    public int LinesShown { get; set; } = 2;

    public double Width { get; set; } = 1000;

    /// <summary>Hide the overlay after this many seconds without new text (0 = never).</summary>
    public double FadeTimeoutSec { get; set; } = 8;

    /// <summary>Saved positions per monitor device name.</summary>
    public Dictionary<string, OverlayPlacement> Placements { get; set; } = [];

    public string? LastMonitor { get; set; }
}

/// <summary>Position relative to the monitor work area (0..1), so it survives resolution/DPI changes.</summary>
public sealed class OverlayPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

public sealed class HotkeySettings
{
    public string ToggleEditMode { get; set; } = "Ctrl+Alt+C";

    public string PauseResume { get; set; } = "Ctrl+Alt+P";

    public string ClearOverlay { get; set; } = "Ctrl+Alt+X";
}

public sealed class TranscriptSettings
{
    public bool AutoSave { get; set; } = true;

    public string? Folder { get; set; }

    public bool Srt { get; set; } = true;

    public bool Txt { get; set; } = true;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }

    public bool StartListeningOnLaunch { get; set; } = true;

    public bool CheckForUpdates { get; set; }

    /// <summary>Debug only: include transcript text in logs.</summary>
    public bool LogTranscriptText { get; set; }
}
