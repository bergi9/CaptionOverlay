using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CaptionOverlay.App.Infrastructure;
using CaptionOverlay.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace CaptionOverlay.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppController? _controller;
    private SerilogLoggerFactory? _loggers;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire(() => Dispatcher.InvokeAsync(() => _controller?.ShowSettings()));
        if (_instance is null)
        {
            Shutdown(0); // the running instance shows its settings window
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(AppPaths.LogsDir, "captionoverlay-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _loggers = new SerilogLoggerFactory(Log.Logger);
        var logger = _loggers.CreateLogger<App>();
        logger.LogInformation("CaptionOverlay {Version} starting from {Path}", UpdateChecker.CurrentVersionText, AppContext.BaseDirectory);

        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show($"Something went wrong: {args.Exception.Message}\n\nDetails are in the log folder.", "CaptionOverlay",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            _controller = new AppController(_loggers);
            await _controller.InitializeAsync(autostart: e.Args.Contains(StartupRegistration.AutostartArgument));
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Startup failed");
            MessageBox.Show($"CaptionOverlay could not start: {ex.Message}", "CaptionOverlay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _controller?.SaveNow();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.SaveNow();
        Log.Information("CaptionOverlay exiting");
        _instance?.Dispose();
        _loggers?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
