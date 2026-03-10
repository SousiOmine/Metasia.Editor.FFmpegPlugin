namespace FFmpegPlugin;

public sealed record FFmpegOutputSettings(
    string VideoPreset,
    string AudioBitrate,
    bool EnableFastStart)
{
    public static FFmpegOutputSettings Default { get; } = new("veryfast", "192k", true);
}
