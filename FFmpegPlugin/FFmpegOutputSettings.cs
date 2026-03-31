namespace FFmpegPlugin;

internal enum FFmpegOutputContainer
{
    Mp4,
    Mkv,
    Mov,
    Avi,
}

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

    internal FFmpegOutputSettings ResolveForContainer(FFmpegOutputContainer container)
    {
        if (container != FFmpegOutputContainer.Avi)
        {
            return this;
        }

        return this with
        {
            VideoCodec = "libx264",
            AudioCodec = "libmp3lame",
            EnableFastStart = false,
        };
    }
}
