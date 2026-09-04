using System.IO.Compression;
using System.Net.Http.Headers;

namespace AKTelaCapture;

internal static class FfmpegManager
{
    private const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private static string ToolDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AKTelaCapture", "tools");
    public static string PathToExe => Path.Combine(ToolDir, "ffmpeg.exe");

    public static async Task<string> EnsureAsync(IProgress<int>? progress = null, CancellationToken token = default)
    {
        if (File.Exists(PathToExe)) return PathToExe;
        Directory.CreateDirectory(ToolDir);
        var zip = Path.Combine(ToolDir, "ffmpeg.zip");
        var temp = Path.Combine(ToolDir, "extract");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AKTelaCapture", "1.0"));
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
            File.Copy(found, PathToExe, true);
            progress?.Report(100);
            return PathToExe;
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }
}
