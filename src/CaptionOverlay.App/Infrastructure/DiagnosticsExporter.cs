using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CaptionOverlay.Core;
using CaptionOverlay.Core.Diagnostics;
using CaptionOverlay.Core.Settings;

namespace CaptionOverlay.App.Infrastructure;

/// <summary>Zips logs, settings (the API key lives elsewhere and is never included) and a hardware summary.</summary>
public static class DiagnosticsExporter
{
    public static string Export(AppSettings settings, string? runtime, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        string zipPath = Path.Combine(targetFolder, $"CaptionOverlay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        if (Directory.Exists(AppPaths.LogsDir))
        {
            foreach (var log in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log"))
            {
                // Logs are open for writing: copy with sharing instead of ZipFile.CreateEntryFromFile.
                using var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var entry = zip.CreateEntry("logs/" + Path.GetFileName(log)).Open();
                source.CopyTo(entry);
            }
        }

        WriteText(zip, "settings.json", JsonSerializer.Serialize(settings, SettingsStore.JsonOptions));
        var summary = new StringBuilder()
            .AppendLine($"CaptionOverlay {UpdateChecker.CurrentVersionText}")
            .AppendLine($"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})")
            .AppendLine($".NET: {Environment.Version}")
            .AppendLine($"Whisper runtime: {runtime ?? "not loaded"}")
            .AppendLine(HardwareInfo.Query().ToString());
        WriteText(zip, "system.txt", summary.ToString());
        return zipPath;
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
