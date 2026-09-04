using System.IO.Compression;
using System.Net.Http.Headers;

namespace AKTelaCapture;

internal static class FfmpegManager
{
    private const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static string ToolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AKTelaCapture", "tools");

    public static string FfmpegPath => Path.Combine(ToolDirectory, "ffmpeg.exe");

    public static async Task<string> EnsureAsync(IProgress<int>? progress = null, CancellationToken token = default)
    {
        if (File.Exists(FfmpegPath)) return FfmpegPath;

        Directory.CreateDirectory(ToolDirectory);
        var zipPath = Path.Combine(ToolDirectory, "ffmpeg-download.zip");
        var extractPath = Path.Combine(ToolDirectory, "ffmpeg-temp");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AKTelaCapture", "0.3"));

            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(zipPath))
            {
                var buffer = new byte[128 * 1024];
                long readTotal = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, token);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    readTotal += read;
                    if (total is > 0)
                        progress?.Report((int)Math.Clamp(readTotal * 100 / total.Value, 0, 100));
                }
            }

            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);
            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);

            var found = Directory.EnumerateFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is null)
                throw new InvalidOperationException("O pacote do encoder foi baixado, mas o ffmpeg.exe não foi encontrado.");

            File.Copy(found, FfmpegPath, overwrite: true);
            progress?.Report(100);
            return FfmpegPath;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
        }
    }
}
