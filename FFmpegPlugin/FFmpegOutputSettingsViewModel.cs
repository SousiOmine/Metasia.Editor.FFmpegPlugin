using ReactiveUI;

namespace FFmpegPlugin;

public sealed class FFmpegOutputSettingsViewModel : ReactiveObject
{
    public IReadOnlyList<string> Presets { get; } =
    [
        "ultrafast",
        "superfast",
        "veryfast",
        "faster",
        "fast",
        "medium",
        "slow",
        "slower",
        "veryslow"
    ];

    public IReadOnlyList<string> AudioBitrates { get; } =
    [
        "96k",
        "128k",
        "160k",
        "192k",
        "256k",
        "320k"
    ];

    public string VideoPreset
    {
        get => _videoPreset;
        set => this.RaiseAndSetIfChanged(ref _videoPreset, value);
    }

    public string AudioBitrate
    {
        get => _audioBitrate;
        set => this.RaiseAndSetIfChanged(ref _audioBitrate, NormalizeAudioBitrate(value));
    }

    public bool EnableFastStart
    {
        get => _enableFastStart;
        set => this.RaiseAndSetIfChanged(ref _enableFastStart, value);
    }

    private string _videoPreset = FFmpegOutputSettings.Default.VideoPreset;
    private string _audioBitrate = FFmpegOutputSettings.Default.AudioBitrate;
    private bool _enableFastStart = FFmpegOutputSettings.Default.EnableFastStart;

    public FFmpegOutputSettings CreateSettings()
    {
        var preset = Presets.Contains(VideoPreset, StringComparer.OrdinalIgnoreCase)
            ? VideoPreset.ToLowerInvariant()
            : FFmpegOutputSettings.Default.VideoPreset;
        return new FFmpegOutputSettings(preset, NormalizeAudioBitrate(AudioBitrate), EnableFastStart);
    }

    private static string NormalizeAudioBitrate(string? value)
    {
        return value switch
        {
            "96k" or "128k" or "160k" or "192k" or "256k" or "320k" => value,
            _ => FFmpegOutputSettings.Default.AudioBitrate,
        };
    }
}
