using System.Runtime.InteropServices;
using MeetingRecorder.Core.Detection;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace MeetingRecorder.Core.Audio;

internal static class AudioDeviceLocator
{
    public static (MMDevice Device, DeviceChoiceReason Reason)? Find(MMDeviceEnumerator enumerator, DataFlow flow,
        string? meetingAppKey, bool fallBackToOtherApps)
    {
        try
        {
            var choice = DeviceSelection.Choose(ReadCandidates(enumerator, flow), meetingAppKey, Environment.ProcessId, fallBackToOtherApps);
            if (choice.EndpointId is { } id) return (enumerator.GetDevice(id), choice.Reason);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Log.Warning(ex, "Couldn't read {Flow} audio sessions; using the default device", flow);
        }
        return enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var device) && device is not null
            ? (device, DeviceChoiceReason.Default)
            : null;
    }

    private static List<EndpointCandidate> ReadCandidates(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        string? defaultId = null;
        if (enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var def) && def is not null)
        {
            using (def) defaultId = def.ID;
        }

        var keys = new Dictionary<int, string?>();
        var candidates = new List<EndpointCandidate>();
        using var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            candidates.Add(new EndpointCandidate(device.ID, device.ID == defaultId, ActiveSessions(device, keys)));
        }
        return candidates;
    }

    private static List<SessionOwner> ActiveSessions(MMDevice device, Dictionary<int, string?> keys)
    {
        var owners = new List<SessionOwner>();
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            using var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession) continue;
                int pid = (int)session.GetProcessID;
                if (!keys.TryGetValue(pid, out var key)) keys[pid] = key = ProcessKeys.KeyFor(pid);
                owners.Add(new SessionOwner(pid, key));
            }
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Couldn't read sessions on {Device}", device.FriendlyName);
        }
        return owners;
    }
}
