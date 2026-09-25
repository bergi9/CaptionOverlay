using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Export;
using CaptionOverlay.Core.Settings;

namespace CaptionOverlay.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "co-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Roundtrips_settings_and_keeps_api_key_out_of_json()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var secrets = new SecretStore(Path.Combine(_dir, "secrets.dat"));
        var settings = new AppSettings();
        settings.Engine.Mode = EngineMode.Api;
        settings.Engine.Language = "de";
        settings.Overlay.Placements["\\\\.\\DISPLAY2"] = new OverlayPlacement { Left = 0.1, Top = 0.8, Width = 0.5, Height = 0.1 };
        store.Save(settings);
        secrets.Set(SecretStore.ApiKeyName("groq"), "gsk_supersecret");

        var loaded = store.Load();
        loaded.Engine.Mode.Should().Be(EngineMode.Api);
        loaded.Engine.Language.Should().Be("de");
        loaded.Overlay.Placements.Should().ContainKey("\\\\.\\DISPLAY2");
        new SecretStore(Path.Combine(_dir, "secrets.dat")).Get(SecretStore.ApiKeyName("groq")).Should().Be("gsk_supersecret");

        File.ReadAllText(store.FilePath).Should().NotContain("gsk_supersecret").And.Contain("\"mode\": \"Api\"");
        File.ReadAllBytes(Path.Combine(_dir, "secrets.dat")).Should().NotContainInConsecutiveOrder("gsk_supersecret"u8.ToArray());
    }

    [Fact]
    public void Broken_settings_fall_back_to_defaults_and_old_schema_migrates()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        new SettingsStore(path).Load().Engine.Language.Should().Be("auto");
        File.Exists(path + ".broken").Should().BeTrue();

        File.WriteAllText(path, """{ "engine": { "language": "fr" } }""");
        var migrated = new SettingsStore(path).Load();
        migrated.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        migrated.Engine.Language.Should().Be("fr");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }
}

public class ExportTests
{
    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(61.5, "00:01:01,500")]
    [InlineData(3723.004, "01:02:03,004")]
    public void Formats_srt_timestamps(double seconds, string expected) =>
        SrtWriter.FormatTime(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void Writes_numbered_srt_entries()
    {
        var sw = new StringWriter();
        using var writer = new SrtWriter(sw);
        writer.Append(new CaptionLine("Hallo.", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), Guid.NewGuid()));
        writer.Append(new CaptionLine("Welt.", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), Guid.NewGuid()));
        sw.ToString().Should().Be(
            "1\r\n00:00:01,000 --> 00:00:02,500\r\nHallo.\r\n\r\n" +
            "2\r\n00:00:03,000 --> 00:00:04,000\r\nWelt.\r\n\r\n");
    }

    [Fact]
    public void Session_writes_are_flushed_immediately()
    {
        string dir = Path.Combine(Path.GetTempPath(), "co-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var session = new TranscriptSession(dir, srt: true, txt: true, new DateTime(2026, 1, 2, 3, 4, 5));
            session.Append(new CaptionLine("Eins.", TimeSpan.Zero, TimeSpan.FromSeconds(1), Guid.NewGuid()));
            string srt = session.BasePath + ".srt";
            Path.GetFileName(srt).Should().Be("2026-01-02_03-04-05.srt");
            using var read = new FileStream(srt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            new StreamReader(read).ReadToEnd().Should().Contain("Eins.");
            using var readTxt = new FileStream(session.BasePath + ".txt", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            new StreamReader(readTxt).ReadToEnd().Should().Be("Eins." + Environment.NewLine);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
