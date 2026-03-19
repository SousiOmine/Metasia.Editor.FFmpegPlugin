using ReactiveUI;

namespace FFmpegPlugin;

public sealed class FFmpegOutputSettingsViewModel : ReactiveObject
{
    public record CodecOption(string Value, string DisplayName);

    public IReadOnlyList<CodecOption> VideoCodecs { get; } =
    [
        new("libx264", "H.264"),
        new("libx265", "H.265"),
        new("libaom-av1", "AV1")
    ];

    public IReadOnlyList<CodecOption> AudioCodecs { get; } =
    [
        new("aac", "AAC"),
        new("libmp3lame", "MP3")
    ];

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

    public string VideoCodec
    {
        get => _videoCodec;
        set => this.RaiseAndSetIfChanged(ref _videoCodec, value);
    }

    public string AudioCodec
    {
        get => _audioCodec;
        set => this.RaiseAndSetIfChanged(ref _audioCodec, value);
    }

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

    public bool UseCustomResolution
    {
        get => _useCustomResolution;
        set => this.RaiseAndSetIfChanged(ref _useCustomResolution, value);
    }

    public int CustomWidth
    {
        get => _customWidth;
        set => this.RaiseAndSetIfChanged(ref _customWidth, NormalizeResolutionDimension(value));
    }

    public int CustomHeight
    {
        get => _customHeight;
        set => this.RaiseAndSetIfChanged(ref _customHeight, NormalizeResolutionDimension(value));
    }

    private string _videoCodec = FFmpegOutputSettings.Default.VideoCodec;
    private string _audioCodec = FFmpegOutputSettings.Default.AudioCodec;
    private string _videoPreset = FFmpegOutputSettings.Default.VideoPreset;
    private string _audioBitrate = FFmpegOutputSettings.Default.AudioBitrate;
    private bool _enableFastStart = FFmpegOutputSettings.Default.EnableFastStart;
    private bool _useCustomResolution = false;
    private int _customWidth = 1920;
    private int _customHeight = 1080;

    public FFmpegOutputSettings CreateSettings()
    {
        var vCodec = IsValidVideoCodec(VideoCodec) ? VideoCodec : FFmpegOutputSettings.Default.VideoCodec;
        var aCodec = IsValidAudioCodec(AudioCodec) ? AudioCodec : FFmpegOutputSettings.Default.AudioCodec;
        var preset = Presets.Contains(VideoPreset, StringComparer.OrdinalIgnoreCase)
            ? VideoPreset.ToLowerInvariant()
            : FFmpegOutputSettings.Default.VideoPreset;

        int? width = UseCustomResolution ? NormalizeResolutionDimension(CustomWidth) : null;
        int? height = UseCustomResolution ? NormalizeResolutionDimension(CustomHeight) : null;

        return new FFmpegOutputSettings(vCodec, aCodec, preset, NormalizeAudioBitrate(AudioBitrate), EnableFastStart, width, height);
    }

    private bool IsValidVideoCodec(string value)
    {
        foreach (var c in VideoCodecs)
        {
            if (c.Value == value) return true;
        }
        return false;
    }

    private bool IsValidAudioCodec(string value)
    {
        foreach (var c in AudioCodecs)
        {
            if (c.Value == value) return true;
        }
        return false;
    }

    private static string NormalizeAudioBitrate(string? value)
    {
        return value switch
        {
            "96k" or "128k" or "160k" or "192k" or "256k" or "320k" => value,
            _ => FFmpegOutputSettings.Default.AudioBitrate,
        };
    }

    private static int NormalizeResolutionDimension(int value)
    {
        value = Math.Max(2, value);
        return value % 2 == 0 ? value : value + 1;
    }
}
