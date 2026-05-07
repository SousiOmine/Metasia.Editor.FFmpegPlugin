namespace FFmpegPlugin;

internal static class FfmpegPathResolver
{
    internal static string Resolve(string pluginDirectory)
    {
        string executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        string pluginPath = Path.Combine(pluginDirectory, executableName);
        if (File.Exists(pluginPath))
        {
            return pluginPath;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
        }

        return pluginPath;
    }
}
