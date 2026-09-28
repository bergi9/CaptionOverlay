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

    /// <summary>
    /// Replaces missing or out-of-range values (hand-edited or damaged files) with safe defaults,
    /// so a bad settings file can never produce e.g. an invisible overlay or a VAD that never triggers.
    /// </summary>
    public AppSettings Sanitize()
    {
        var defaults = new AppSettings();
        Audio ??= defaults.Audio;
        Engine ??= defaults.Engine;
        Api ??= defaults.Api;
        Overlay ??= defaults.Overlay;
        Hotkeys ??= defaults.Hotkeys;
        Transcripts ??= defaults.Transcripts;
        General ??= defaults.General;

        if (Audio.SpeechThreshold is < 0.1f or > 0.95f)
        {
            Audio.SpeechThreshold = defaults.Audio.SpeechThreshold;
        }
        if (Audio.SilenceThreshold < 0.05f || Audio.SilenceThreshold > Audio.SpeechThreshold)
        {
            Audio.SilenceThreshold = Math.Min(defaults.Audio.SilenceThreshold, Audio.SpeechThreshold);
        }
        if (Audio.EndSilenceMs is < 200 or > 5000)
        {
            Audio.EndSilenceMs = defaults.Audio.EndSilenceMs;
        }
        if (Audio.MaxUtteranceSec is < 3 or > 30)
        {
            Audio.MaxUtteranceSec = defaults.Audio.MaxUtteranceSec;
        }

        if (string.IsNullOrWhiteSpace(General.UiLanguage))
        {
            General.UiLanguage = defaults.General.UiLanguage;
        }
        if (!Enum.IsDefined(General.Theme))
        {
            General.Theme = defaults.General.Theme;
        }
        if (!Enum.IsDefined(Transcripts.Split))
        {
            Transcripts.Split = defaults.Transcripts.Split;
        }
        if (Transcripts.SplitAfterMinutes is < 1 or > 1440)
        {
            Transcripts.SplitAfterMinutes = defaults.Transcripts.SplitAfterMinutes;
        }

        if (string.IsNullOrWhiteSpace(Engine.Language))
        {
            Engine.Language = defaults.Engine.Language;
        }
        if (Engine.PartialIntervalMs is < 200 or > 10000)
        {
            Engine.PartialIntervalMs = defaults.Engine.PartialIntervalMs;
        }

        var o = Overlay;
        var d = defaults.Overlay;
        if (string.IsNullOrWhiteSpace(o.FontFamily))
        {
            o.FontFamily = d.FontFamily;
        }
        if (o.FontSize is < 8 or > 120)
        {
            o.FontSize = d.FontSize;
        }
        if (o.FontWeight is < 100 or > 950)
        {
            o.FontWeight = d.FontWeight;
        }
        o.TextColor = string.IsNullOrWhiteSpace(o.TextColor) ? d.TextColor : o.TextColor;
        o.OutlineColor = string.IsNullOrWhiteSpace(o.OutlineColor) ? d.OutlineColor : o.OutlineColor;
        o.BackgroundColor = string.IsNullOrWhiteSpace(o.BackgroundColor) ? d.BackgroundColor : o.BackgroundColor;
        o.OutlineThickness = Math.Clamp(o.OutlineThickness, 0, 10);
        o.BackgroundOpacity = Math.Clamp(o.BackgroundOpacity, 0, 1);
        if (o.LinesShown is < 1 or > 8)
        {
            o.LinesShown = d.LinesShown;
        }
        if (o.DefaultWidthFraction is < 0.15 or > 1)
        {
            o.DefaultWidthFraction = d.DefaultWidthFraction;
        }
        o.FadeTimeoutSec = Math.Clamp(o.FadeTimeoutSec, 0, 600);
        o.Placements ??= [];
        return this;
    }
}

public sealed class AudioSettings
{
    /// <summary>Output device id, or null to follow the system default.</summary>
    public string? DeviceId { get; set; }

    public bool UseVad { get; set; } = true;

    public float SpeechThreshold { get; set; } = 0.5f;

    public float SilenceThreshold { get; set; } = 0.35f;

    public int EndSilenceMs { get; set; } = 600;

    /// <summary>App default 8 s (the engine default stays 12 s): best accuracy in the German end-to-end test, see ADR-024.</summary>
    public double MaxUtteranceSec { get; set; } = 8;
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

    public double BackgroundOpacity { get; set; } = 0.7;

    public int LinesShown { get; set; } = 2;

    /// <summary>Default overlay width as a fraction of the monitor work area (until the user resizes it).</summary>
    public double DefaultWidthFraction { get; set; } = 0.6;

    /// <summary>Hide the overlay after this many seconds without new text (0 = never).</summary>
    public double FadeTimeoutSec { get; set; } = 8;

    /// <summary>Slide lines in and out instead of switching instantly (independent of the Windows animation setting).</summary>
    public bool AnimateLines { get; set; } = true;

    /// <summary>Show sound tags the model writes ([Music], (Applause), ♪) instead of removing them; helps deaf viewers.</summary>
    public bool ShowSoundTags { get; set; }

    /// <summary>Saved positions per monitor device name.</summary>
    public Dictionary<string, OverlayPlacement> Placements { get; set; } = [];

    public string? LastMonitor { get; set; }
}

/// <summary>
/// Overlay position as fractions (0..1) of the monitor work area, so it survives resolution and DPI
/// changes. The overlay grows upwards, so its bottom edge is the anchor.
/// </summary>
public sealed class OverlayPlacement
{
    public double Left { get; set; }

    public double Bottom { get; set; }

    public double Width { get; set; }
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

    /// <summary>When a new transcript file starts (besides "Split now").</summary>
    public TranscriptSplit Split { get; set; } = TranscriptSplit.AfterBreak;

    /// <summary>For <see cref="TranscriptSplit.AfterBreak"/>: minutes without captions that start a new file.</summary>
    public int SplitAfterMinutes { get; set; } = 30;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }

    public bool StartListeningOnLaunch { get; set; } = true;

    public bool CheckForUpdates { get; set; }

    /// <summary>Debug only: include transcript text in logs.</summary>
    public bool LogTranscriptText { get; set; }

    /// <summary>UI language: "system" (Windows display language, English if untranslated), "en" or "de".</summary>
    public string UiLanguage { get; set; } = Loc.SystemLanguage;

    public AppTheme Theme { get; set; } = AppTheme.System;
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    /// <summary>Follow the Windows light/dark app mode.</summary>
    System,
    Light,
    Dark,
}

[JsonConverter(typeof(JsonStringEnumConverter<TranscriptSplit>))]
public enum TranscriptSplit
{
    /// <summary>A new file after a break without captions (<see cref="TranscriptSettings.SplitAfterMinutes"/>); stopping and starting within it continues the file.</summary>
    AfterBreak,

    /// <summary>A new file each time listening starts.</summary>
    EachStart,

    /// <summary>Only "Split now" (and restarting the app) start a new file.</summary>
    Manual,
}
