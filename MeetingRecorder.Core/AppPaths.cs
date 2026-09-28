namespace MeetingRecorder.Core;

public sealed class AppPaths
{
    public const string AppName = "MeetingRecorder";

    public AppPaths(string root)
    {
        Root = root;
        RecordingsDirectory = Path.Combine(root, "Recordings");
        PartialDirectory = Path.Combine(RecordingsDirectory, ".partial");
        LogsDirectory = Path.Combine(root, "logs");
        IndexFile = Path.Combine(root, "recordings.json");
        SettingsFile = Path.Combine(root, "settings.json");
    }

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public string Root { get; }
    public string RecordingsDirectory { get; }
    public string PartialDirectory { get; }
    public string LogsDirectory { get; }
    public string IndexFile { get; }
    public string SettingsFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RecordingsDirectory);
        Directory.CreateDirectory(PartialDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
