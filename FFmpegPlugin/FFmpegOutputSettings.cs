namespace FFmpegPlugin;

public sealed record FFmpegOutputSettings(
    string VideoCodec,
    string AudioCodec,
    string VideoPreset,
    string AudioBitrate,
    bool EnableFastStart,
    int? OutputWidth,
    int? OutputHeight)
{
    public static FFmpegOutputSettings Default { get; } = new("libx264", "aac", "veryfast", "192k", true, null, null);
}
