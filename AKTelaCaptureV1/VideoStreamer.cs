using System.Diagnostics;

namespace AKTelaCapture;

internal sealed class VideoStreamer : IAsyncDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _task;
    public bool IsRunning => _task is { IsCompleted: false };
    public event Action<byte[]>? PacketReady;
    public event Action<double>? FpsChanged;
    public event Action<string>? EncoderChanged;
    public event Action<string>? StreamError;

    public Task StartAsync(CaptureSource source, StreamConfig config)
    {
        if (IsRunning) return Task.CompletedTask;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(source, config, _cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts; var task = _task; _cts = null; _task = null;
        if (cts is null) return;
        cts.Cancel();
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { if (task is not null) await Task.WhenAny(task, Task.Delay(1200)); } catch { }
        cts.Dispose(); FpsChanged?.Invoke(0);
    }

    private async Task RunAsync(CaptureSource source, StreamConfig cfg, CancellationToken token)
    {
        try
        {
            var ffmpeg = await FfmpegManager.EnsureAsync(null, token);
            var attempts = new List<(string Name, ProcessStartInfo Psi)>
            {
                ("NVENC · D3D11", BuildDdaNvenc(ffmpeg, source, cfg, true)),
                ("NVENC · D3D11 compatível", BuildDdaNvenc(ffmpeg, source, cfg, false)),
                ("Media Foundation · D3D11", BuildDdaMf(ffmpeg, source, cfg)),
                ("NVENC · compatibilidade", BuildGdiNvenc(ffmpeg, source, cfg)),
                ("Media Foundation · compatibilidade", BuildGdiMf(ffmpeg, source, cfg))
            };
            var errors = new List<string>();
            foreach (var attempt in attempts)
            {
                if (token.IsCancellationRequested) return;
                var result = await RunAttempt(attempt.Name, attempt.Psi, cfg, token);
                if (result.Ok || token.IsCancellationRequested) return;
                errors.Add($"{attempt.Name}: {result.Error}");
            }
            StreamError?.Invoke(string.Join(Environment.NewLine + Environment.NewLine, errors));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StreamError?.Invoke(ex.Message); }
    }

    private async Task<(bool Ok, string Error)> RunAttempt(string name, ProcessStartInfo psi, StreamConfig cfg, CancellationToken token)
    {
        using var process = new Process { StartInfo = psi };
        _process = process;
        if (!process.Start()) return (false, "Não foi possível iniciar o FFmpeg.");
        EncoderChanged?.Invoke(name);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        var reader = new H264AccessUnitReader();
        var buffer = new byte[128 * 1024];
        var fpsClock = Stopwatch.StartNew(); int frames = 0; long totalFrames = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token);
                if (read <= 0) break;
                foreach (var (data, key) in reader.Push(buffer, read))
                {
                    frames++; totalFrames++;
                    PacketReady?.Invoke(PacketProtocol.Create(MediaKind.Video, key, MediaClock.NowMicroseconds(), 1_000_000 / cfg.Fps, data));
                }
                if (fpsClock.ElapsedMilliseconds >= 1000)
                {
                    FpsChanged?.Invoke(frames * 1000d / fpsClock.ElapsedMilliseconds);
                    frames = 0; fpsClock.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return (true, string.Empty); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
        await process.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;
        if (totalFrames > 0 && process.ExitCode == 0) return (true, string.Empty);
        var detail = string.Join(" | ", stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).TakeLast(10));
        return (false, string.IsNullOrWhiteSpace(detail) ? "encerrou sem gerar vídeo" : detail);
    }

    private static ProcessStartInfo Base(string exe) => new(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    private static void Add(ProcessStartInfo p, params string[] args) { foreach (var a in args) p.ArgumentList.Add(a); }
    private static int Even(int v) => Math.Max(2, v & ~1);

    private static (int W, int H) DdaInput(ProcessStartInfo p, CaptureSource src, StreamConfig cfg)
    {
        var parts = new List<string> { $"output_idx={Math.Max(0, src.OutputIndex)}", $"framerate={cfg.Fps}", "draw_mouse=0", "dup_frames=1" };
        var w = Even(src.Width); var h = Even(src.Height);
        if (src.Kind == SourceKind.Window)
        {
            var monitor = Screen.FromRectangle(src.Bounds).Bounds;
            var x = Math.Clamp(src.Bounds.Left - monitor.Left, 0, Math.Max(0, monitor.Width - 2));
            var y = Math.Clamp(src.Bounds.Top - monitor.Top, 0, Math.Max(0, monitor.Height - 2));
            w = Even(Math.Clamp(w, 2, Math.Max(2, monitor.Width - x)));
            h = Even(Math.Clamp(h, 2, Math.Max(2, monitor.Height - y)));
            parts.Add($"video_size={w}x{h}"); parts.Add($"offset_x={x}"); parts.Add($"offset_y={y}");
        }
        Add(p, "-hide_banner", "-loglevel", "warning", "-f", "lavfi", "-i", $"ddagrab={string.Join(':', parts)}");
        return (w, h);
    }

    private static void Nvenc(ProcessStartInfo p, StreamConfig cfg)
    {
        var buf = Math.Max(160, cfg.BitrateMbps * 1000 / Math.Max(1, cfg.Fps));
        Add(p, "-c:v", "h264_nvenc", "-preset", cfg.Fps >= 60 ? "p3" : "p4", "-tune", "ull", "-rc", "cbr", "-b:v", $"{cfg.BitrateMbps}M", "-maxrate", $"{cfg.BitrateMbps}M", "-bufsize", $"{buf}k", "-bf", "0", "-rc-lookahead", "0", "-zerolatency", "1", "-g", cfg.Fps.ToString(), "-profile:v", "high", "-r:v", cfg.Fps.ToString(), "-fps_mode", "cfr", "-bsf:v", "h264_metadata=aud=insert", "-flush_packets", "1", "-f", "h264", "pipe:1");
    }
    private static void Mf(ProcessStartInfo p, StreamConfig cfg)
    {
        var buf = Math.Max(160, cfg.BitrateMbps * 1000 / Math.Max(1, cfg.Fps));
        Add(p, "-c:v", "h264_mf", "-hw_encoding", "1", "-scenario", "display_remoting", "-rate_control", "cbr", "-b:v", $"{cfg.BitrateMbps}M", "-maxrate", $"{cfg.BitrateMbps}M", "-bufsize", $"{buf}k", "-g", cfg.Fps.ToString(), "-bf", "0", "-r:v", cfg.Fps.ToString(), "-fps_mode", "cfr", "-bsf:v", "h264_metadata=aud=insert", "-flush_packets", "1", "-f", "h264", "pipe:1");
    }

    private static ProcessStartInfo BuildDdaNvenc(string exe, CaptureSource src, StreamConfig cfg, bool gpuScale)
    {
        var p = Base(exe); DdaInput(p, src, cfg);
        if (gpuScale)
            Add(p, "-vf", $"scale_d3d11=width={Even(cfg.Width)}:height={Even(cfg.Height)}:format=nv12");
        else
            Add(p, "-vf", $"hwdownload,format=bgra,scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=nv12");
        Nvenc(p, cfg); return p;
    }
    private static ProcessStartInfo BuildDdaMf(string exe, CaptureSource src, StreamConfig cfg)
    {
        var p = Base(exe); DdaInput(p, src, cfg);
        Add(p, "-vf", $"hwdownload,format=bgra,scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=nv12");
        Mf(p, cfg); return p;
    }
    private static void GdiInput(ProcessStartInfo p, CaptureSource src, StreamConfig cfg)
    {
        Add(p, "-hide_banner", "-loglevel", "warning", "-f", "gdigrab", "-framerate", cfg.Fps.ToString(), "-draw_mouse", "0");
        if (src.Kind == SourceKind.Window) Add(p, "-i", $"hwnd={unchecked((ulong)src.WindowHandle.ToInt64())}");
        else Add(p, "-offset_x", src.Bounds.Left.ToString(), "-offset_y", src.Bounds.Top.ToString(), "-video_size", $"{src.Width}x{src.Height}", "-i", "desktop");
        Add(p, "-vf", $"scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=nv12");
    }
    private static ProcessStartInfo BuildGdiNvenc(string exe, CaptureSource src, StreamConfig cfg) { var p = Base(exe); GdiInput(p, src, cfg); Nvenc(p, cfg); return p; }
    private static ProcessStartInfo BuildGdiMf(string exe, CaptureSource src, StreamConfig cfg) { var p = Base(exe); GdiInput(p, src, cfg); Mf(p, cfg); return p; }

    public async ValueTask DisposeAsync() => await StopAsync();
}
