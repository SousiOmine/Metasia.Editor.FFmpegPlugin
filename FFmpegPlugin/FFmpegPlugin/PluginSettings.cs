using System.Diagnostics;
using System.Text.Json;

namespace FFmpegPlugin;

internal sealed class PluginSettings
{
    private static readonly HashSet<string> SupportedHwAccelApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto",
        "none",
        "vdpau",
        "dxva2",
        "d3d11va",
        "vaapi",
        "qsv",
        "videotoolbox",
        "cuda"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal const string FileName = "settings.jsonc";

    public DecodeSettings Decode { get; init; } = new();

    internal bool HardwareDecodeEnabled => Decode.HardwareDecode ?? true;
    internal string HardwareDecodeApi => NormalizeHardwareDecodeApi(Decode.HardwareDecodeApi);

    internal static PluginSettings Load(string pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return new PluginSettings();
        }

        var settingsPath = Path.Combine(pluginDirectory, FileName);
        if (!File.Exists(settingsPath))
        {
            return new PluginSettings();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<PluginSettings>(json, JsonOptions);
            return settings ?? new PluginSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"settings.jsonc の読み込みに失敗しました: {settingsPath}, {ex.Message}");
            return new PluginSettings();
        }
    }

    public sealed class DecodeSettings
    {
        public bool? HardwareDecode { get; init; }
        public string? HardwareDecodeApi { get; init; }
    }

    private static string NormalizeHardwareDecodeApi(string? api)
    {
        if (string.IsNullOrWhiteSpace(api))
        {
            return "auto";
        }

        var normalized = api.Trim().ToLowerInvariant();
        if (SupportedHwAccelApis.Contains(normalized))
        {
            return normalized;
        }

        Debug.WriteLine($"未対応の hardwareDecodeApi '{api}' が指定されました。'auto' を使用します。");
        return "auto";
    }
}
