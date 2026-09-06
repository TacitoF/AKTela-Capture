using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace AKTelaCapture;

internal static class FfmpegManager
{
    private const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-9.0.zip";
    private const string RequiredBuildId = "n9-gfxcapture-2026-07";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string ToolDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AKTelaCapture", "tools");
    private static string BuildMarker => Path.Combine(ToolDir, "ffmpeg-build.txt");
    public static string PathToExe => Path.Combine(ToolDir, "ffmpeg.exe");
    public static bool IsCurrent
    {
        get
        {
            if (!File.Exists(PathToExe) || !File.Exists(BuildMarker)) return false;
            try { return string.Equals(File.ReadAllText(BuildMarker).Trim(), RequiredBuildId, StringComparison.Ordinal); }
            catch { return false; }
        }
    }

    public static async Task<string> EnsureAsync(IProgress<int>? progress = null, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (IsCurrent) return PathToExe;

            Directory.CreateDirectory(ToolDir);
            var operation = Guid.NewGuid().ToString("N");
            var zip = Path.Combine(ToolDir, $"ffmpeg-{operation}.zip");
            var temp = Path.Combine(ToolDir, $"extract-{operation}");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AKTelaCapture", "2.6"));
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = File.Create(zip))
                {
                    var buffer = new byte[128 * 1024]; long done = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, token); if (read <= 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), token); done += read;
                        if (total is > 0) progress?.Report((int)Math.Clamp(done * 100 / total.Value, 0, 100));
                    }
                }
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                ZipFile.ExtractToDirectory(zip, temp, true);
                var found = Directory.EnumerateFiles(temp, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
                            ?? throw new InvalidOperationException("ffmpeg.exe não encontrado no pacote baixado.");

                if (!await SupportsGfxCaptureAsync(found, token))
                    throw new InvalidOperationException("O FFmpeg baixado não oferece a captura moderna de janelas (gfxcapture).");

                File.Copy(found, PathToExe, true);
                await File.WriteAllTextAsync(BuildMarker, RequiredBuildId, token);
                progress?.Report(100);
                return PathToExe;
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task<bool> SupportsGfxCaptureAsync(string executable, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-filters");

        using var process = new Process { StartInfo = start };
        if (!process.Start()) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdout) + (await stderr);
            return process.ExitCode == 0 && output.Contains("gfxcapture", StringComparison.Ordinal);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return false;
        }
    }
}
