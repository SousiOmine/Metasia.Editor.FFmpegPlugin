namespace FFmpegPlugin.Decode;

internal static class DecodeTime
{
    private static readonly TimeSpan DefaultFrameDuration = TimeSpan.FromMilliseconds(16.666);

    internal static TimeSpan ResolveFrameDuration(double framerate)
    {
        return framerate > 0
            ? TimeSpan.FromSeconds(1.0 / framerate)
            : DefaultFrameDuration;
    }

    internal static TimeSpan ResolveSeekTolerance(TimeSpan frameDuration)
    {
        if (frameDuration <= TimeSpan.Zero)
        {
            return DefaultFrameDuration;
        }

        return TimeSpan.FromTicks(Math.Max(1, frameDuration.Ticks - 1));
    }

    internal static TimeSpan ClampToMedia(TimeSpan time, TimeSpan duration, TimeSpan frameDuration)
    {
        if (time < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration <= TimeSpan.Zero)
        {
            return time;
        }

        var maxTime = duration - frameDuration;
        if (maxTime <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return time > maxTime ? maxTime : time;
    }
}
