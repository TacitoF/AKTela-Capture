using System.Diagnostics;

namespace AKTelaCapture;

internal sealed class VideoStreamer : IAsyncDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private int _fps;
    private int _outputIndex;
    private int _bitrateMbps;
    private long _frames;
    private readonly Stopwatch _fpsClock = new();

    public event Action<byte[]>? PacketReady;
    public event Action<double>? FpsChanged;
    public event Action<string>? EncoderChanged;
    public event Action<string>? StreamError;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync(int outputIndex, int fps, CancellationToken token = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _outputIndex = Math.Max(0, outputIndex);
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
            var attempts = new[]
            {
                (Name: "GPU • NVIDIA NVENC", Builder: (Func<ProcessStartInfo>)(() => BuildNvenc(ffmpeg))),
                (Name: "GPU • Media Foundation", Builder: (Func<ProcessStartInfo>)(() => BuildMediaFoundation(ffmpeg)))
            };

            var errors = new List<string>();
            foreach (var attempt in attempts)
            {
                if (token.IsCancellationRequested) return;
                var result = await RunAttemptAsync(attempt.Name, attempt.Builder(), token);
                if (result.CompletedNormally || token.IsCancellationRequested) return;
                errors.Add($"{attempt.Name}: {result.ErrorMessage}");
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
                        var elapsed = Math.Max(0.001, _fpsClock.Elapsed.TotalSeconds);
                        var count = Interlocked.Exchange(ref _frames, 0);
                        FpsChanged?.Invoke(count / elapsed);
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

        var lastLines = string.Join(" ", stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
        return (false, string.IsNullOrWhiteSpace(lastLines)
            ? $"{name} encerrou sem gerar vídeo."
            : lastLines.Trim());
    }

    private ProcessStartInfo BaseStartInfo(string ffmpeg)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };

        Add(psi, "-hide_banner", "-loglevel", "warning",
            "-f", "lavfi",
            "-i", $"ddagrab=output_idx={_outputIndex}:framerate={_fps}:draw_mouse=1:dup_frames=1",
            "-vf", "scale_d3d11=width=1920:height=1080:format=nv12");
        return psi;
    }

    private ProcessStartInfo BuildMediaFoundation(string ffmpeg)
    {
        var psi = BaseStartInfo(ffmpeg);
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
            "-fps_mode", "passthrough",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264",
            "pipe:1");
        return psi;
    }

    private ProcessStartInfo BuildNvenc(string ffmpeg)
    {
        var psi = BaseStartInfo(ffmpeg);
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
            "-fps_mode", "passthrough",
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
