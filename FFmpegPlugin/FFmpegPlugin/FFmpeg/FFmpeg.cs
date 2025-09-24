using System.Diagnostics;
using System.Reflection;

namespace FFmpegPlugin.FFmpeg;

public class FFmpeg
{
    public FFmpeg()
    {
        try
        {
            string? pluginDirectory = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]);
            ProcessStartInfo processStartInfo = new ProcessStartInfo()
            {
                FileName = pluginDirectory + "\\ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process? process = Process.Start(processStartInfo))
            {
                process?.WaitForExit();
            }
            
        }
        catch (Exception e)
        {
            throw new Exception("Failed to load FFmpeg: " + e.Message);
        }
    }
    
    public byte[] GetBitmapFromFrame()
    {
        return [];
    }
}