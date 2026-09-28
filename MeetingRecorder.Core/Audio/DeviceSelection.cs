using MeetingRecorder.Core.Detection;

namespace MeetingRecorder.Core.Audio;

internal readonly record struct SessionOwner(int ProcessId, string? AppKey);

internal sealed record EndpointCandidate(string Id, bool IsDefault, IReadOnlyList<SessionOwner> ActiveSessions);

public enum DeviceChoiceReason
{
    MeetingApp,

    OtherApp,

    Default,
}

internal readonly record struct DeviceChoice(string? EndpointId, DeviceChoiceReason Reason);

internal static class DeviceSelection
{
    public static DeviceChoice Choose(IReadOnlyList<EndpointCandidate> endpoints, string? meetingAppKey, int selfProcessId,
        bool fallBackToOtherApps)
    {
        bool IsOther(SessionOwner s) => s.ProcessId != 0 && s.ProcessId != selfProcessId;

        if (meetingAppKey is not null)
        {
            var used = endpoints.Where(e => e.ActiveSessions.Any(s =>
                IsOther(s) && s.AppKey is { } key && AppNames.IsSameKey(key, meetingAppKey))).ToList();
            if (PreferDefault(used) is { } id) return new DeviceChoice(id, DeviceChoiceReason.MeetingApp);
        }

        if (fallBackToOtherApps)
        {
            var used = endpoints.Where(e => e.ActiveSessions.Any(IsOther)).ToList();
            if (PreferDefault(used) is { } id) return new DeviceChoice(id, DeviceChoiceReason.OtherApp);
        }

        return new DeviceChoice(null, DeviceChoiceReason.Default);
    }

    private static string? PreferDefault(List<EndpointCandidate> used) =>
        (used.FirstOrDefault(e => e.IsDefault) ?? used.FirstOrDefault())?.Id;
}
