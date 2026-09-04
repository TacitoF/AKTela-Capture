using System.Diagnostics;

namespace AKTelaCapture;

internal sealed class VideoStreamer : IAsyncDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private int _fps;
    private int _outputIndex;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _bitrateMbps;
    private long _frames;
    private readonly Stopwatch _fpsClock = new();

    public event Action<byte[]>? PacketReady;
    public event Action<double>? FpsChanged;
    public event Action<string>? EncoderChanged;
    public event Action<string>? StreamError;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync(int outputIndex, int sourceWidth, int sourceHeight, int fps, CancellationToken token = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _outputIndex = Math.Max(0, outputIndex);
        _sourceWidth = Math.Max(2, sourceWidth);
        _sourceHeight = Math.Max(2, sourceHeight);
        _fps = fps >= 60 ? 60 : 30;
        _bitrateMbps = _fps == 60 ? 12 : 8;
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
            var attempts = new List<(string Name, Func<ProcessStartInfo> Builder)>();

            // Em 1080p não precisamos do scale_d3d11. Isso evita uma etapa D3D11
            // que pode falhar antes mesmo do encoder receber o primeiro frame.
            if (_sourceWidth == 1920 && _sourceHeight == 1080)
            {
                attempts.Add(("GPU • NVIDIA NVENC", () => BuildNvencDirectDda(ffmpeg)));
            }
            else
            {
                attempts.Add(("GPU • NVIDIA NVENC", () => BuildNvencScaledDda(ffmpeg)));
            }

            // Fallback realmente independente: usa o capturador GDI do FFmpeg.
            // É um pouco mais pesado que DXGI, mas evita que um problema em ddagrab/
            // scale_d3d11 derrube também o encoder alternativo.
            attempts.Add(("GPU • NVIDIA NVENC (compatibilidade)", () => BuildNvencGdi(ffmpeg)));
            attempts.Add(("GPU • Media Foundation (compatibilidade)", () => BuildMediaFoundationGdi(ffmpeg)));

            var errors = new List<string>();
            foreach (var attempt in attempts)
            {
                if (token.IsCancellationRequested) return;
                var result = await RunAttemptAsync(attempt.Name, attempt.Builder(), token);
                if (result.CompletedNormally || token.IsCancellationRequested) return;
                errors.Add($"{attempt.Name}:{Environment.NewLine}{result.ErrorMessage}");
            }

            StreamError?.Invoke(errors.Count == 0
                ? "Não foi possível iniciar o encoder de vídeo."
                : string.Join(Environment.NewLine + Environment.NewLine, errors));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StreamError?.Invoke(ex.Message);
        }
    }

    private async Task<(bool CompletedNormally, string ErrorMessage)> RunAttemptAsync(
        string name, ProcessStartInfo psi, CancellationToken token)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        if (!process.Start()) return (false, $"Falha ao iniciar {name}.");
        EncoderChanged?.Invoke(name);

        var stderrTask = process.StandardError.ReadToEndAsync(token);
        var reader = new H264AccessUnitReader();
        var buffer = new byte[128 * 1024];
        var emitted = 0L;
        _frames = 0;
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
                    Interlocked.Increment(ref _frames);
                    var packet = MediaPacket.Create(
                        MediaKind.Video,
                        keyFrame,
                        MediaClock.NowMicroseconds(),
                        1_000_000 / _fps,
                        data);
                    PacketReady?.Invoke(packet);

                    if (_fpsClock.ElapsedMilliseconds >= 1000)
                    {
                        // O fluxo de saída é forçado a CFR pelo FFmpeg. Alguns encoders
                        // Media Foundation podem expor mais de um access unit por quadro,
                        // então contar NAL/AUD pode mostrar 2x o FPS real. A UI exibe a
                        // taxa efetivamente solicitada ao encoder, evitando essa leitura falsa.
                        Interlocked.Exchange(ref _frames, 0);
                        FpsChanged?.Invoke(_fps);
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

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .TakeLast(14);
        var detail = string.Join(Environment.NewLine, lines);
        return (false, string.IsNullOrWhiteSpace(detail)
            ? $"{name} encerrou sem gerar vídeo."
            : detail);
    }

    private ProcessStartInfo NewStartInfo(string ffmpeg)
    {
        return new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
    }

    private void AddDdaInput(ProcessStartInfo psi)
    {
        Add(psi,
            "-hide_banner", "-loglevel", "warning",
            "-f", "lavfi",
            "-i", $"ddagrab=output_idx={_outputIndex}:framerate={_fps}:draw_mouse=0:dup_frames=1");
    }

    private void AddGdiInput(ProcessStartInfo psi)
    {
        Add(psi,
            "-hide_banner", "-loglevel", "warning",
            "-f", "gdigrab",
            "-framerate", _fps.ToString(),
            "-draw_mouse", "0",
            "-video_size", $"{_sourceWidth}x{_sourceHeight}",
            "-i", "desktop");

        // Mantém a saída em 1080p. Quando a origem já é 1080p, o scale é evitado.
        if (_sourceWidth != 1920 || _sourceHeight != 1080)
            Add(psi, "-vf", "scale=1920:1080:flags=fast_bilinear,format=nv12");
        else
            Add(psi, "-vf", "format=nv12");
    }

    private void AddNvencOptions(ProcessStartInfo psi)
    {
        var bufferK = Math.Max(128, _bitrateMbps * 1000 / _fps);
        Add(psi,
            "-c:v", "h264_nvenc",
            "-preset", _fps == 60 ? "p3" : "p4",
            "-tune", "ull",
            "-rc", "cbr",
            "-b:v", $"{_bitrateMbps}M",
            "-maxrate", $"{_bitrateMbps}M",
            "-bufsize", $"{bufferK}k",
            "-bf", "0",
            "-rc-lookahead", "0",
            "-zerolatency", "1",
            "-g", _fps.ToString(),
            "-profile:v", "high",
            "-level:v", "4.2",
            "-spatial-aq", "1",
            "-r:v", _fps.ToString(),
            "-fps_mode", "cfr",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264",
            "pipe:1");
    }

    private ProcessStartInfo BuildNvencDirectDda(string ffmpeg)
    {
        var psi = NewStartInfo(ffmpeg);
        AddDdaInput(psi);
        AddNvencOptions(psi);
        return psi;
    }

    private ProcessStartInfo BuildNvencScaledDda(string ffmpeg)
    {
        var psi = NewStartInfo(ffmpeg);
        AddDdaInput(psi);
        Add(psi, "-vf", "scale_d3d11=width=1920:height=1080:format=nv12");
        AddNvencOptions(psi);
        return psi;
    }

    private ProcessStartInfo BuildNvencGdi(string ffmpeg)
    {
        var psi = NewStartInfo(ffmpeg);
        AddGdiInput(psi);
        AddNvencOptions(psi);
        return psi;
    }

    private ProcessStartInfo BuildMediaFoundationGdi(string ffmpeg)
    {
        var psi = NewStartInfo(ffmpeg);
        AddGdiInput(psi);
        var bufferK = Math.Max(128, _bitrateMbps * 1000 / _fps);
        Add(psi,
            "-c:v", "h264_mf",
            "-hw_encoding", "1",
            "-scenario", "display_remoting",
            "-rate_control", "cbr",
            "-b:v", $"{_bitrateMbps}M",
            "-maxrate", $"{_bitrateMbps}M",
            "-bufsize", $"{bufferK}k",
            "-g", _fps.ToString(),
            "-bf", "0",
            "-r:v", _fps.ToString(),
            "-fps_mode", "cfr",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264",
            "pipe:1");
        return psi;
    }

    private static void Add(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args) psi.ArgumentList.Add(arg);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
