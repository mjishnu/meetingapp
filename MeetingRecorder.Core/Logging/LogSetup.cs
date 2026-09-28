using System.Globalization;
using Serilog;

namespace MeetingRecorder.Core.Logging;

public static class LogSetup
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} [{ThreadId,3}] {Message:l}{NewLine}{Exception}";

    public static void Initialize(string directory)
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.WithThreadId()
            .WriteTo.Async(a =>
            {
                a.File(Path.Combine(directory, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: OutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture);
                a.Debug(outputTemplate: OutputTemplate, formatProvider: CultureInfo.InvariantCulture);
            })
            .CreateLogger();
    }
}
