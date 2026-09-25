using System.Globalization;
using CaptionOverlay.Cli;
using CaptionOverlay.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var options = CliArgs.Parse(args);
if (options.Command is null or "help" || options.Flags.ContainsKey("help"))
{
    Commands.PrintHelp();
    return 0;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(options.Flags.ContainsKey("verbose") ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(Path.Combine(AppPaths.LogsDir, "cli-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .WriteTo.Logger(l => l
        .MinimumLevel.Is(options.Flags.ContainsKey("verbose") ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console(standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose, formatProvider: CultureInfo.InvariantCulture))
    .CreateLogger();

using var loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await Commands.RunAsync(options, loggerFactory, cts.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (CliException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    loggerFactory.CreateLogger("cli").LogError(ex, "Command failed");
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
