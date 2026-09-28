namespace MeetingRecorder.Core.Audio;

internal static class Mixer
{
    private const float Knee = 0.8f;

    public static float SoftClip(float v)
    {
        float a = Math.Abs(v);
        if (a <= Knee) return v;
        float over = (a - Knee) / (1f - Knee);
        float y = Knee + (1f - Knee) * (over / (1f + over));
        return v < 0 ? -y : y;
    }
}
