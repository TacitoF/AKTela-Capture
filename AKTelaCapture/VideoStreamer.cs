using System.Diagnostics;

namespace AKTelaCapture;

internal sealed class VideoStreamer : IAsyncDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private CaptureSourceOption? _source;
    private StreamConfig? _config;
    private readonly Stopwatch _fpsClock = new();

    public event Action<byte[]>? PacketReady;
    public event Action<double>? FpsChanged;
    public event Action<string>? EncoderChanged;
    public event Action<string>? StreamError;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync(CaptureSourceOption source, StreamConfig config, CancellationToken token = default)
    {
        if (IsRunning) return Task.CompletedTask;
        _source = source;
        _config = config;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var task = _runTask;
        _cts = null;
        _runTask = null;
        if (cts is null) return;
        cts.Cancel();
        try
        {
            if (_process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            if (task is not null) await Task.WhenAny(task, Task.Delay(1500));
        }
        catch { }
        cts.Dispose();
        FpsChanged?.Invoke(0);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var ffmpeg = await FfmpegManager.EnsureAsync(null, token);
            var source = _source ?? throw new InvalidOperationException("Fonte de vídeo não selecionada.");
            var config = _config ?? throw new InvalidOperationException("Configuração de vídeo não definida.");
            var attempts = new List<(string Name, Func<ProcessStartInfo> Builder)>();

            // Desktop Duplication captura a composição do desktop pela GPU e não sofre da tela preta
            // do GDI ao tentar ler superfícies D3D11/D3D12 de jogos como GTA.
            if (source.Kind == CaptureSourceKind.Window)
            {
                attempts.Add(("NVIDIA NVENC · captura de jogo", () => BuildNvencDda(ffmpeg, source, config)));
                attempts.Add(("Media Foundation · captura de jogo", () => BuildMediaFoundationDda(ffmpeg, source, config)));
            }
            else
            {
                attempts.Add(("NVIDIA NVENC", () => BuildNvencDda(ffmpeg, source, config)));
                attempts.Add(("Media Foundation", () => BuildMediaFoundationDda(ffmpeg, source, config)));
            }

            // GDI fica somente como último fallback para apps comuns. Para jogos acelerados por GPU
            // ele pode retornar uma imagem preta, por isso nunca é mais a primeira tentativa.
            attempts.Add(("NVIDIA NVENC · compatibilidade", () => BuildNvencGdi(ffmpeg, source, config)));
            attempts.Add(("Media Foundation · compatibilidade", () => BuildMediaFoundationGdi(ffmpeg, source, config)));

            var errors = new List<string>();
            foreach (var attempt in attempts)
            {
                if (token.IsCancellationRequested) return;
                var result = await RunAttemptAsync(attempt.Name, attempt.Builder(), config, token);
                if (result.CompletedNormally || token.IsCancellationRequested) return;
                errors.Add($"{attempt.Name}:{Environment.NewLine}{result.ErrorMessage}");
            }

            StreamError?.Invoke(errors.Count == 0
                ? "Não foi possível iniciar o encoder de vídeo."
                : string.Join(Environment.NewLine + Environment.NewLine, errors));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StreamError?.Invoke(ex.Message); }
    }

    private async Task<(bool CompletedNormally, string ErrorMessage)> RunAttemptAsync(
        string name, ProcessStartInfo psi, StreamConfig config, CancellationToken token)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        if (!process.Start()) return (false, $"Falha ao iniciar {name}.");
        EncoderChanged?.Invoke(name);

        var stderrTask = process.StandardError.ReadToEndAsync(token);
        var reader = new H264AccessUnitReader();
        var buffer = new byte[128 * 1024];
        var emitted = 0L;
        _fpsClock.Restart();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token);
                if (read <= 0) break;

                foreach (var (data, keyFrame) in reader.Push(buffer, read))
                {
                    emitted++;
                    PacketReady?.Invoke(MediaPacket.Create(
                        MediaKind.Video,
                        keyFrame,
                        MediaClock.NowMicroseconds(),
                        1_000_000 / config.Fps,
                        data));

                    if (_fpsClock.ElapsedMilliseconds >= 1000)
                    {
                        FpsChanged?.Invoke(config.Fps);
                        _fpsClock.Restart();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (true, string.Empty);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;
        if (emitted > 0 && process.ExitCode == 0) return (true, string.Empty);

        var detail = string.Join(Environment.NewLine,
            stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .TakeLast(18));
        return (false, string.IsNullOrWhiteSpace(detail) ? $"{name} encerrou sem gerar vídeo." : detail);
    }

    private static ProcessStartInfo NewStartInfo(string ffmpeg) => new(ffmpeg)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false
    };

    private static (int Width, int Height) AddDdaInput(ProcessStartInfo psi, CaptureSourceOption source, StreamConfig config)
    {
        var parts = new List<string>
        {
            $"output_idx={Math.Max(0, source.FfmpegOutputIndex)}",
            $"framerate={config.Fps}",
            "draw_mouse=0",
            "dup_frames=1"
        };

        var captureWidth = MakeEven(Math.Max(2, source.Width));
        var captureHeight = MakeEven(Math.Max(2, source.Height));

        if (source.Kind == CaptureSourceKind.Window)
        {
            var monitor = source.CaptureDisplayBounds;
            if (monitor.Width <= 0 || monitor.Height <= 0)
                monitor = Screen.FromRectangle(source.ScreenBounds).Bounds;

            var localX = Math.Clamp(source.ScreenBounds.Left - monitor.Left, 0, Math.Max(0, monitor.Width - 2));
            var localY = Math.Clamp(source.ScreenBounds.Top - monitor.Top, 0, Math.Max(0, monitor.Height - 2));
            captureWidth = MakeEven(Math.Clamp(captureWidth, 2, Math.Max(2, monitor.Width - localX)));
            captureHeight = MakeEven(Math.Clamp(captureHeight, 2, Math.Max(2, monitor.Height - localY)));

            parts.Add($"video_size={captureWidth}x{captureHeight}");
            parts.Add($"offset_x={localX}");
            parts.Add($"offset_y={localY}");
        }

        Add(psi,
            "-hide_banner", "-loglevel", "warning",
            "-f", "lavfi",
            "-i", $"ddagrab={string.Join(":", parts)}");

        return (captureWidth, captureHeight);
    }

    private static int MakeEven(int value) => Math.Max(2, value & ~1);

    private static void AddDdaSoftwareDownloadAndScale(ProcessStartInfo psi, int sourceWidth, int sourceHeight, StreamConfig config)
    {
        var targetWidth = MakeEven(config.Width);
        var targetHeight = MakeEven(config.Height);
        var filter = sourceWidth == targetWidth && sourceHeight == targetHeight
            ? "hwdownload,format=bgra,format=nv12"
            : "hwdownload,format=bgra," + BuildScaleFilter(sourceWidth, sourceHeight, targetWidth, targetHeight);
        Add(psi, "-vf", filter);
    }

    private static void AddDdaScaleForNvencIfNeeded(ProcessStartInfo psi, int sourceWidth, int sourceHeight, StreamConfig config)
    {
        if (sourceWidth == config.Width && sourceHeight == config.Height)
            return; // zero-copy D3D11 -> NVENC quando não há redimensionamento

        Add(psi, "-vf", "hwdownload,format=bgra," + BuildScaleFilter(sourceWidth, sourceHeight, config.Width, config.Height));
    }

    private static void AddGdiInput(ProcessStartInfo psi, CaptureSourceOption source, StreamConfig config)
    {
        Add(psi, "-hide_banner", "-loglevel", "warning", "-f", "gdigrab", "-framerate", config.Fps.ToString(), "-draw_mouse", "0");
        if (source.Kind == CaptureSourceKind.Window)
        {
            var hwndValue = unchecked((ulong)source.WindowHandle.ToInt64());
            Add(psi, "-i", $"hwnd={hwndValue}");
        }
        else
        {
            Add(psi,
                "-offset_x", source.ScreenBounds.Left.ToString(),
                "-offset_y", source.ScreenBounds.Top.ToString(),
                "-video_size", $"{Math.Max(2, source.Width)}x{Math.Max(2, source.Height)}",
                "-i", "desktop");
        }

        Add(psi, "-vf", BuildScaleFilter(source.Width, source.Height, config.Width, config.Height));
    }

    private static string BuildScaleFilter(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        targetWidth = MakeEven(targetWidth);
        targetHeight = MakeEven(targetHeight);
        if (sourceWidth == targetWidth && sourceHeight == targetHeight) return "format=nv12";
        return $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease:flags=fast_bilinear," +
               $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black,format=nv12";
    }

    private static void AddNvencOptions(ProcessStartInfo psi, StreamConfig config)
    {
        var bufferK = Math.Max(128, config.VideoBitrateMbps * 1000 / config.Fps);
        Add(psi,
            "-c:v", "h264_nvenc",
            "-preset", config.Fps >= 60 ? "p3" : "p4",
            "-tune", "ull",
            "-rc", "cbr",
            "-b:v", $"{config.VideoBitrateMbps}M",
            "-maxrate", $"{config.VideoBitrateMbps}M",
            "-bufsize", $"{bufferK}k",
            "-bf", "0",
            "-rc-lookahead", "0",
            "-zerolatency", "1",
            "-g", config.Fps.ToString(),
            "-profile:v", "high",
            "-spatial-aq", "1",
            "-r:v", config.Fps.ToString(),
            "-fps_mode", "cfr",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264", "pipe:1");
    }

    private static void AddMediaFoundationOptions(ProcessStartInfo psi, StreamConfig config)
    {
        var bufferK = Math.Max(128, config.VideoBitrateMbps * 1000 / config.Fps);
        Add(psi,
            "-c:v", "h264_mf",
            "-hw_encoding", "1",
            "-scenario", "display_remoting",
            "-rate_control", "cbr",
            "-b:v", $"{config.VideoBitrateMbps}M",
            "-maxrate", $"{config.VideoBitrateMbps}M",
            "-bufsize", $"{bufferK}k",
            "-g", config.Fps.ToString(),
            "-bf", "0",
            "-r:v", config.Fps.ToString(),
            "-fps_mode", "cfr",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264", "pipe:1");
    }

    private static ProcessStartInfo BuildNvencDda(string ffmpeg, CaptureSourceOption source, StreamConfig config)
    {
        var psi = NewStartInfo(ffmpeg);
        var size = AddDdaInput(psi, source, config);
        AddDdaScaleForNvencIfNeeded(psi, size.Width, size.Height, config);
        AddNvencOptions(psi, config);
        return psi;
    }

    private static ProcessStartInfo BuildMediaFoundationDda(string ffmpeg, CaptureSourceOption source, StreamConfig config)
    {
        var psi = NewStartInfo(ffmpeg);
        var size = AddDdaInput(psi, source, config);
        AddDdaSoftwareDownloadAndScale(psi, size.Width, size.Height, config);
        AddMediaFoundationOptions(psi, config);
        return psi;
    }

    private static ProcessStartInfo BuildNvencGdi(string ffmpeg, CaptureSourceOption source, StreamConfig config)
    {
        var psi = NewStartInfo(ffmpeg);
        AddGdiInput(psi, source, config);
        AddNvencOptions(psi, config);
        return psi;
    }

    private static ProcessStartInfo BuildMediaFoundationGdi(string ffmpeg, CaptureSourceOption source, StreamConfig config)
    {
        var psi = NewStartInfo(ffmpeg);
        AddGdiInput(psi, source, config);
        AddMediaFoundationOptions(psi, config);
        return psi;
    }

    private static void Add(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args) psi.ArgumentList.Add(arg);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
