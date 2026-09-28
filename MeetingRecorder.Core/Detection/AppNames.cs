namespace MeetingRecorder.Core.Detection;

public static class AppNames
{
    private static readonly Dictionary<string, string> ByExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zoom.exe"] = "Zoom",
        ["ms-teams.exe"] = "Microsoft Teams",
        ["teams.exe"] = "Microsoft Teams",
        ["slack.exe"] = "Slack",
        ["chrome.exe"] = "Google Chrome",
        ["msedge.exe"] = "Microsoft Edge",
        ["firefox.exe"] = "Firefox",
        ["discord.exe"] = "Discord",
        ["webex.exe"] = "Webex",
        ["ciscocollabhost.exe"] = "Webex",
        ["skype.exe"] = "Skype",
        ["brave.exe"] = "Brave",
        ["vivaldi.exe"] = "Vivaldi",
        ["opera.exe"] = "Opera",
        ["obs64.exe"] = "OBS Studio",
    };

    private static readonly Dictionary<string, string> ByPackage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSTeams"] = "Microsoft Teams",
        ["MicrosoftTeams"] = "Microsoft Teams",
        ["5319275A.WhatsAppDesktop"] = "WhatsApp",
        ["Microsoft.SkypeApp"] = "Skype",
        ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder",
        ["Microsoft.WindowsCamera"] = "Camera",
        ["Microsoft.ScreenSketch"] = "Snipping Tool",
        ["Microsoft.Copilot"] = "Copilot",
    };

    public static string RawName(string key)
    {
        if (key.StartsWith(MicAppUsage.NonPackagedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = key[MicAppUsage.NonPackagedPrefix.Length..];
            int hash = path.LastIndexOf('#');
            return hash >= 0 ? path[(hash + 1)..] : path;
        }
        int underscore = key.IndexOf('_');
        return underscore > 0 ? key[..underscore] : key;
    }

    public static string? FriendlyName(string key)
    {
        var raw = RawName(key);
        var table = key.StartsWith(MicAppUsage.NonPackagedPrefix, StringComparison.OrdinalIgnoreCase) ? ByExe : ByPackage;
        return table.TryGetValue(raw, out var name) ? name : null;
    }

    public static string KeyForExecutable(string exePath) =>
        MicAppUsage.NonPackagedPrefix + Path.GetFullPath(exePath).Replace('\\', '#');

    public static bool IsSameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
