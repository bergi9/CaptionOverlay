using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CaptionOverlay.Core.Settings;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace CaptionOverlay.App.Tray;

/// <summary>Tray icon + context menu. The menu is rebuilt on every open so it always reflects the current state.</summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly AppController _app;
    private readonly TaskbarIcon _icon;
    private Action? _notificationClick;

    public TrayIconManager(AppController app)
    {
        _app = app;
        _icon = new TaskbarIcon
        {
            ToolTipText = "CaptionOverlay",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = new ContextMenu(),
            NoLeftClickDelay = true,
        };
        _icon.PreviewTrayContextMenuOpen += (_, _) => BuildMenu();
        _icon.TrayLeftMouseUp += (_, _) => _app.ShowSettings();
        _icon.TrayBalloonTipClicked += (_, _) => _notificationClick?.Invoke();
        // Efficiency mode (EcoQoS) would throttle the whole process, which hurts real-time audio: keep it off.
        _icon.ForceCreate(enablesEfficiencyMode: false);
        BuildMenu();
    }

    public void SetToolTip(string text) => _icon.ToolTipText = text.Length > 120 ? text[..120] : text;

    public void Notify(string title, string message, Action? onClick = null)
    {
        _notificationClick = onClick;
        try
        {
            _icon.ShowNotification(title, message, NotificationIcon.Info);
        }
        catch (InvalidOperationException)
        {
            // Tray not ready (explorer restarting): notifications are best effort.
        }
    }

    public void Dispose() => _icon.Dispose();

    private void BuildMenu()
    {
        var menu = _icon.ContextMenu!;
        menu.Items.Clear();

        menu.Items.Add(Item(Loc.Get(_app.IsListening ? "Tray_StopListening" : "Tray_StartListening"), async () => await _app.ToggleListeningAsync(), bold: true));
        var pause = Item(Loc.Get("Tray_Pause"), _app.TogglePause);
        pause.IsCheckable = true;
        pause.IsChecked = _app.IsPaused;
        pause.IsEnabled = _app.IsListening;
        menu.Items.Add(pause);
        menu.Items.Add(Item(Loc.Get("Tray_EditOverlay"), _app.ToggleEditMode));
        menu.Items.Add(Item(Loc.Get("Tray_ClearOverlay"), _app.ClearOverlay));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildEngineMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.Get("Tray_CopyTranscript"), _app.CopyTranscript));
        var split = Item(Loc.Get("Tray_SplitTranscript"), _app.SplitTranscriptNow);
        split.IsEnabled = _app.Settings.Transcripts.AutoSave;
        menu.Items.Add(split);
        menu.Items.Add(Item(Loc.Get("Tray_OpenTranscripts"), _app.OpenTranscriptsFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.Get("Tray_Settings"), () => _app.ShowSettings()));
        menu.Items.Add(Item(Loc.Get("Tray_Quit"), async () => await _app.QuitAsync()));
    }

    private MenuItem BuildEngineMenu()
    {
        var engine = _app.Settings.Engine;
        var installed = _app.ModelStore.GetInstalled(_app.Catalog);
        string current = engine.Mode == EngineMode.Api
            ? Loc.Format("Tray_Api", CaptionOverlay.Core.Transcription.ApiProviderPreset.Find(_app.Settings.Api.Provider).Name)
            : Loc.Format("Tray_Local", installed.FirstOrDefault(m => m.Id == engine.ModelId)?.DisplayName ?? Loc.Get("Tray_NoModel"));
        var root = new MenuItem { Header = Loc.Format("Tray_Engine", current) };

        foreach (var model in installed)
        {
            var item = Item(Loc.Format("Tray_Local", model.DisplayName), () => _app.SwitchEngine(EngineMode.Local, model.Id));
            item.IsCheckable = true;
            item.IsChecked = engine.Mode == EngineMode.Local && engine.ModelId == model.Id;
            root.Items.Add(item);
        }
        if (installed.Count == 0)
        {
            root.Items.Add(new MenuItem { Header = Loc.Get("Tray_NoLocalModels"), IsEnabled = false });
        }
        root.Items.Add(new Separator());
        var api = Item(Loc.Format("Tray_Api", CaptionOverlay.Core.Transcription.ApiProviderPreset.Find(_app.Settings.Api.Provider).Name), () => _app.SwitchEngine(EngineMode.Api));
        api.IsCheckable = true;
        api.IsChecked = engine.Mode == EngineMode.Api;
        root.Items.Add(api);
        root.Items.Add(new Separator());
        root.Items.Add(Item(Loc.Get("Tray_ManageModels"), () => _app.ShowSettings("Models")));
        return root;
    }

    private static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold)
        {
            item.FontWeight = FontWeights.SemiBold;
        }
        item.Click += (_, _) => action();
        return item;
    }
}
