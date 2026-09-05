using System.Diagnostics;

namespace AKTelaCapture;

internal sealed class VideoStreamer : IAsyncDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private CaptureSource? _source;
    private StreamConfig? _config;
    private long _frames;
    private long _keyframes;
    private long _restarts;
    private double _fps;
    private string _encoder = "—";
    private string _codec = "—";
    private string _profile = "—";
    private string _codecString = "—";

    public bool IsRunning => _task is { IsCompleted: false };

    public event Action<byte[]>? PacketReady;
    public event Action<double>? FpsChanged;
    public event Action<string>? EncoderChanged;
    public event Action<string, string, string>? CodecChanged;
    public event Action<string>? StreamError;

    public async Task StartAsync(CaptureSource source, StreamConfig config)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (IsRunning) return;
            _source = source;
            _config = config;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _task = Task.Run(() => RunAsync(source, config, token));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestartAsync(CaptureSource source, StreamConfig config)
    {
        Interlocked.Increment(ref _restarts);
        await StopAsync();
        await StartAsync(source, config);
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            var cts = _cts;
            var task = _task;
            _cts = null;
            _task = null;

            if (cts is null) return;
            cts.Cancel();
            try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
            try { if (task is not null) await Task.WhenAny(task, Task.Delay(1300)); } catch { }
            cts.Dispose();
            _process = null;
            _fps = 0;
            FpsChanged?.Invoke(0);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<string> ProbeAsync(StreamConfig config, CancellationToken token = default)
    {
        var ffmpeg = await FfmpegManager.EnsureAsync(null, token);
        if (config.VideoCodec == "vp8")
        {
            var ok = await ProbeVp8(ffmpeg, config, token);
            if (!ok) throw new InvalidOperationException("O encoder VP8 de compatibilidade não iniciou.");
            return "Software VP8";
        }

        var attempts = new (string Name, string Encoder)[]
        {
            ("NVENC", "h264_nvenc"),
            ("Media Foundation", "h264_mf"),
            ("Software H.264", "libx264")
        };

        var errors = new List<string>();
        foreach (var attempt in attempts)
        {
            try
            {
                var info = await ProbeH264(ffmpeg, config, attempt.Encoder, token);
                if (info is not null && IsCompatible(config, info, out _)) return attempt.Name;
                if (info is not null) errors.Add($"{attempt.Name}: gerou {info.CodecString}");
                else errors.Add($"{attempt.Name}: não gerou SPS H.264");
            }
            catch (Exception ex)
            {
                errors.Add($"{attempt.Name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Nenhum encoder produziu o perfil H.264 necessário. " + string.Join(" | ", errors));
    }

    private async Task RunAsync(CaptureSource source, StreamConfig cfg, CancellationToken token)
    {
        try
        {
            var ffmpeg = await FfmpegManager.EnsureAsync(null, token);
            var attempts = cfg.VideoCodec == "vp8"
                ? Vp8Attempts(ffmpeg, source, cfg)
                : H264Attempts(ffmpeg, source, cfg);

            var errors = new List<string>();
            foreach (var attempt in attempts)
            {
                if (token.IsCancellationRequested) return;
                var result = cfg.VideoCodec == "vp8"
                    ? await RunVp8Attempt(attempt.Name, attempt.Psi, cfg, token)
                    : await RunH264Attempt(attempt.Name, attempt.Psi, cfg, token);

                if (result.Ok || token.IsCancellationRequested) return;
                errors.Add($"{attempt.Name}: {result.Error}");
            }

            StreamError?.Invoke(string.Join(Environment.NewLine + Environment.NewLine, errors));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StreamError?.Invoke(ex.Message); }
    }

    private static List<(string Name, ProcessStartInfo Psi)> H264Attempts(string ffmpeg, CaptureSource source, StreamConfig cfg) =>
    [
        // O scale_d3d11 direto foi removido do caminho padrão: alguns drivers/GPUs
        // retornam E_INVALIDARG ao criar a textura de saída. Mantemos Desktop
        // Duplication para captura, baixamos o frame para memória e usamos NVENC
        // para codificar por hardware. É um pouco menos zero-copy, mas muito mais estável.
        ("NVENC · Desktop Duplication", BuildDdaNvenc(ffmpeg, source, cfg, false)),
        ("NVENC · compatibilidade", BuildGdiNvenc(ffmpeg, source, cfg)),
        ("Media Foundation · Desktop Duplication", BuildDdaMf(ffmpeg, source, cfg)),
        ("Media Foundation · compatibilidade", BuildGdiMf(ffmpeg, source, cfg)),
        ("Software H.264 · compatibilidade", BuildGdiX264(ffmpeg, source, cfg))
    ];

    private static List<(string Name, ProcessStartInfo Psi)> Vp8Attempts(string ffmpeg, CaptureSource source, StreamConfig cfg) =>
    [
        ("Software VP8 · D3D11", BuildDdaVp8(ffmpeg, source, cfg)),
        ("Software VP8 · compatibilidade", BuildGdiVp8(ffmpeg, source, cfg))
    ];

    private async Task<(bool Ok, string Error)> RunH264Attempt(string name, ProcessStartInfo psi, StreamConfig cfg, CancellationToken token)
    {
        using var process = new Process { StartInfo = psi };
        _process = process;
        if (!process.Start()) return (false, "Não foi possível iniciar o FFmpeg.");

        SetEncoder(name);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        var reader = new H264AccessUnitReader();
        var buffer = new byte[128 * 1024];
        var fpsClock = Stopwatch.StartNew();
        var framesThisSecond = 0;
        var validated = false;
        string? validationError = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token);
                if (read <= 0) break;

                foreach (var (data, key) in reader.Push(buffer, read))
                {
                    if (!validated)
                    {
                        if (!key) continue;
                        var info = H264AccessUnitReader.Inspect(data) ?? reader.StreamInfo;
                        if (info is null) continue;
                        if (!IsCompatible(cfg, info, out var incompatibility))
                        {
                            validationError = incompatibility;
                            try { if (!process.HasExited) process.Kill(true); } catch { }
                            break;
                        }

                        validated = true;
                        SetCodec("h264", info.ProfileName, info.CodecString);
                    }

                    if (!validated) continue;
                    framesThisSecond++;
                    Interlocked.Increment(ref _frames);
                    if (key) Interlocked.Increment(ref _keyframes);
                    PacketReady?.Invoke(PacketProtocol.Create(MediaKind.Video, key, MediaClock.NowMicroseconds(), 1_000_000 / Math.Max(1, cfg.Fps), data));
                }

                if (validationError is not null) break;
                UpdateFps(fpsClock, ref framesThisSecond);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (true, string.Empty);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }

        try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        var stderr = await SafeAwait(stderrTask);
        if (validationError is not null) return (false, validationError);
        if (validated && token.IsCancellationRequested) return (true, string.Empty);

        var detail = Tail(stderr);
        return (false, string.IsNullOrWhiteSpace(detail) ? "encoder encerrou antes de manter o vídeo" : detail);
    }

    private async Task<(bool Ok, string Error)> RunVp8Attempt(string name, ProcessStartInfo psi, StreamConfig cfg, CancellationToken token)
    {
        using var process = new Process { StartInfo = psi };
        _process = process;
        if (!process.Start()) return (false, "Não foi possível iniciar o FFmpeg.");

        SetEncoder(name);
        SetCodec("vp8", "compatibilidade", "vp8");
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        var reader = new IvfFrameReader();
        var buffer = new byte[128 * 1024];
        var fpsClock = Stopwatch.StartNew();
        var framesThisSecond = 0;
        var gotFrames = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token);
                if (read <= 0) break;

                foreach (var (data, key) in reader.Push(buffer, read))
                {
                    gotFrames = true;
                    framesThisSecond++;
                    Interlocked.Increment(ref _frames);
                    if (key) Interlocked.Increment(ref _keyframes);
                    PacketReady?.Invoke(PacketProtocol.Create(MediaKind.Video, key, MediaClock.NowMicroseconds(), 1_000_000 / Math.Max(1, cfg.Fps), data));
                }
                UpdateFps(fpsClock, ref framesThisSecond);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (true, string.Empty);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }

        try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        var stderr = await SafeAwait(stderrTask);
        if (gotFrames && token.IsCancellationRequested) return (true, string.Empty);
        var detail = Tail(stderr);
        return (false, string.IsNullOrWhiteSpace(detail) ? "encoder VP8 encerrou sem vídeo" : detail);
    }

    private void UpdateFps(Stopwatch clock, ref int frames)
    {
        if (clock.ElapsedMilliseconds < 1000) return;
        _fps = frames * 1000d / Math.Max(1, clock.ElapsedMilliseconds);
        FpsChanged?.Invoke(_fps);
        frames = 0;
        clock.Restart();
    }

    private void SetEncoder(string value)
    {
        _encoder = value;
        EncoderChanged?.Invoke(value);
    }

    private void SetCodec(string codec, string profile, string codecString)
    {
        _codec = codec;
        _profile = profile;
        _codecString = codecString;
        CodecChanged?.Invoke(codec, profile, codecString);
    }

    public VideoDiagnostics GetDiagnostics() => new(
        _encoder,
        _codec,
        _profile,
        _codecString,
        _fps,
        Interlocked.Read(ref _frames),
        Interlocked.Read(ref _keyframes),
        Interlocked.Read(ref _restarts));

    private static bool IsCompatible(StreamConfig cfg, H264StreamInfo info, out string error)
    {
        var wantedProfile = cfg.VideoProfile.ToLowerInvariant();
        var profileOk = wantedProfile switch
        {
            "baseline" => info.ProfileIdc == 66,
            "main" => info.ProfileIdc == 77,
            "high" => info.ProfileIdc == 100,
            _ => false
        };

        if (!profileOk)
        {
            error = $"perfil solicitado {wantedProfile}, mas o encoder gerou {info.ProfileName} ({info.CodecString})";
            return false;
        }

        var maxLevel = H264LevelIdc(cfg);
        if (info.LevelIdc > maxLevel)
        {
            error = $"o encoder gerou nível H.264 {info.LevelIdc / 10d:0.0}, acima do nível negociado {maxLevel / 10d:0.0}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task<H264StreamInfo?> ProbeH264(string exe, StreamConfig cfg, string encoder, CancellationToken token)
    {
        var p = Base(exe);
        Add(p, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30", "-frames:v", "1");
        if (encoder == "h264_nvenc") Nvenc(p, cfg);
        else if (encoder == "h264_mf") Mf(p, cfg);
        else X264(p, cfg);

        using var process = new Process { StartInfo = p };
        if (!process.Start()) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(2500);
        await using var ms = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }
        return H264AccessUnitReader.Inspect(ms.ToArray());
    }

    private static async Task<bool> ProbeVp8(string exe, StreamConfig cfg, CancellationToken token)
    {
        var p = Base(exe);
        Add(p, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=320x180:r=30", "-frames:v", "1");
        Vp8(p, cfg);
        using var process = new Process { StartInfo = p };
        if (!process.Start()) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(2500);
        var header = new byte[4];
        try
        {
            var read = await process.StandardOutput.BaseStream.ReadAsync(header, timeout.Token);
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return read == 4 && header[0] == (byte)'D' && header[1] == (byte)'K' && header[2] == (byte)'I' && header[3] == (byte)'F';
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return false;
        }
    }

    private static ProcessStartInfo Base(string exe) => new(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void Add(ProcessStartInfo p, params string[] args)
    {
        foreach (var arg in args) p.ArgumentList.Add(arg);
    }

    private static int Even(int value) => Math.Max(2, value & ~1);

    private static void DdaInput(ProcessStartInfo p, CaptureSource src, StreamConfig cfg)
    {
        var parts = new List<string>
        {
            $"output_idx={Math.Max(0, src.OutputIndex)}",
            $"framerate={cfg.Fps}",
            "draw_mouse=0",
            "dup_frames=1"
        };

        if (src.Kind == SourceKind.Window)
        {
            var monitor = Screen.FromRectangle(src.Bounds).Bounds;
            var x = Math.Clamp(src.Bounds.Left - monitor.Left, 0, Math.Max(0, monitor.Width - 2));
            var y = Math.Clamp(src.Bounds.Top - monitor.Top, 0, Math.Max(0, monitor.Height - 2));
            var width = Even(Math.Clamp(src.Width, 2, Math.Max(2, monitor.Width - x)));
            var height = Even(Math.Clamp(src.Height, 2, Math.Max(2, monitor.Height - y)));
            parts.Add($"video_size={width}x{height}");
            parts.Add($"offset_x={x}");
            parts.Add($"offset_y={y}");
        }

        Add(p, "-hide_banner", "-loglevel", "warning", "-f", "lavfi", "-i", $"ddagrab={string.Join(':', parts)}");
    }

    private static void GdiInput(ProcessStartInfo p, CaptureSource src, StreamConfig cfg)
    {
        Add(p, "-hide_banner", "-loglevel", "warning", "-f", "gdigrab", "-framerate", cfg.Fps.ToString(), "-draw_mouse", "0");
        if (src.Kind == SourceKind.Window)
            Add(p, "-i", $"hwnd={unchecked((ulong)src.WindowHandle.ToInt64())}");
        else
            Add(p, "-offset_x", src.Bounds.Left.ToString(), "-offset_y", src.Bounds.Top.ToString(), "-video_size", $"{src.Width}x{src.Height}", "-i", "desktop");
    }

    private static string H264Level(StreamConfig cfg) => H264LevelIdc(cfg) switch
    {
        31 => "3.1",
        32 => "3.2",
        40 => "4.0",
        42 => "4.2",
        _ => "3.1"
    };

    private static int H264LevelIdc(StreamConfig cfg) => cfg.QualityKey switch
    {
        "720p60" => 32,
        "1080p30" => 40,
        "1080p60" => 42,
        _ => 31
    };

    private static string ProfileName(StreamConfig cfg) => cfg.VideoProfile switch
    {
        "main" => "main",
        "high" => "high",
        _ => "baseline"
    };

    // Media Foundation usa os IDs numéricos de AVCodecContext.
    // NVENC e libx264 possuem opções privadas e recebem o nome do perfil.
    private static string ProfileId(StreamConfig cfg) => cfg.VideoProfile switch
    {
        "main" => "77",
        "high" => "100",
        _ => "66"
    };

    private static void Nvenc(ProcessStartInfo p, StreamConfig cfg)
    {
        var buf = Math.Max(160, cfg.BitrateMbps * 1000 / Math.Max(1, cfg.Fps));
        Add(p,
            "-c:v", "h264_nvenc",
            "-preset", cfg.Fps >= 60 ? "p3" : "p4",
            "-tune", "ull",
            "-rc", "cbr",
            "-b:v", $"{cfg.BitrateMbps}M",
            "-maxrate", $"{cfg.BitrateMbps}M",
            "-bufsize", $"{buf}k",
            "-bf", "0",
            "-rc-lookahead", "0",
            "-zerolatency", "1",
            "-forced-idr", "1",
            "-strict_gop", "1",
            "-g", cfg.Fps.ToString(),
            // NVENC possui opções privadas nomeadas para profile/level.
            "-profile:v", ProfileName(cfg),
            "-level:v", H264Level(cfg),
            "-r:v", cfg.Fps.ToString(),
            "-fps_mode", "cfr",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264",
            "pipe:1");
    }

    private static void Mf(ProcessStartInfo p, StreamConfig cfg)
    {
        var buf = Math.Max(160, cfg.BitrateMbps * 1000 / Math.Max(1, cfg.Fps));
        Add(p,
            "-c:v", "h264_mf",
            "-hw_encoding", "1",
            "-scenario", "display_remoting",
            "-rate_control", "cbr",
            "-b:v", $"{cfg.BitrateMbps}M",
            "-maxrate", $"{cfg.BitrateMbps}M",
            "-bufsize", $"{buf}k",
            "-g", cfg.Fps.ToString(),
            "-bf", "0",
            "-profile:v", ProfileId(cfg),
            "-level:v", H264LevelIdc(cfg).ToString(),
            "-r:v", cfg.Fps.ToString(),
            "-fps_mode", "cfr",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264",
            "pipe:1");
    }

    private static void X264(ProcessStartInfo p, StreamConfig cfg)
    {
        var buf = Math.Max(160, cfg.BitrateMbps * 1000 / Math.Max(1, cfg.Fps));
        var profile = ProfileName(cfg);
        Add(p,
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-profile:v", profile,
            "-level:v", H264Level(cfg),
            // ultrafast desativa CABAC e 8x8dct. O perfil é um limite de recursos,
            // então habilite os recursos necessários para gerar o SPS negociado.
            "-coder", profile == "baseline" ? "vlc" : "ac",
            "-8x8dct", profile == "high" ? "1" : "0",
            "-b:v", $"{cfg.BitrateMbps}M",
            "-maxrate", $"{cfg.BitrateMbps}M",
            "-bufsize", $"{buf}k",
            "-bf", "0",
            "-g", cfg.Fps.ToString(),
            "-keyint_min", cfg.Fps.ToString(),
            "-sc_threshold", "0",
            "-r:v", cfg.Fps.ToString(),
            "-fps_mode", "cfr",
            "-x264-params", "repeat-headers=1:open-gop=0:scenecut=0",
            "-bsf:v", "h264_metadata=aud=insert",
            "-flush_packets", "1",
            "-f", "h264",
            "pipe:1");
    }

    private static void Vp8(ProcessStartInfo p, StreamConfig cfg)
    {
        var bitrate = Math.Min(cfg.BitrateMbps, 5);
        Add(p,
            "-c:v", "libvpx",
            "-deadline", "realtime",
            "-cpu-used", "8",
            "-lag-in-frames", "0",
            "-auto-alt-ref", "0",
            "-error-resilient", "1",
            "-b:v", $"{bitrate}M",
            "-maxrate", $"{bitrate}M",
            "-bufsize", $"{Math.Max(500, bitrate * 500)}k",
            "-g", Math.Max(1, cfg.Fps).ToString(),
            "-r:v", cfg.Fps.ToString(),
            "-fps_mode", "cfr",
            "-f", "ivf",
            "pipe:1");
    }

    private static ProcessStartInfo BuildDdaNvenc(string exe, CaptureSource src, StreamConfig cfg, bool gpuScale)
    {
        var p = Base(exe);
        DdaInput(p, src, cfg);
        if (gpuScale)
            Add(p, "-vf", $"scale_d3d11=width={Even(cfg.Width)}:height={Even(cfg.Height)}:format=nv12");
        else
            Add(p, "-vf", $"hwdownload,format=bgra,scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=nv12");
        Nvenc(p, cfg);
        return p;
    }

    private static ProcessStartInfo BuildDdaMf(string exe, CaptureSource src, StreamConfig cfg)
    {
        var p = Base(exe);
        DdaInput(p, src, cfg);
        Add(p, "-vf", $"hwdownload,format=bgra,scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=nv12");
        Mf(p, cfg);
        return p;
    }

    private static ProcessStartInfo BuildGdiNvenc(string exe, CaptureSource src, StreamConfig cfg)
    {
        var p = Base(exe);
        GdiInput(p, src, cfg);
        Add(p, "-vf", $"scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=nv12");
        Nvenc(p, cfg);
        return p;
    }

    private static ProcessStartInfo BuildGdiMf(string exe, CaptureSource src, StreamConfig cfg)
    {
        var p = Base(exe);
        GdiInput(p, src, cfg);
        Add(p, "-vf", $"scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=nv12");
        Mf(p, cfg);
        return p;
    }

    private static ProcessStartInfo BuildGdiX264(string exe, CaptureSource src, StreamConfig cfg)
    {
        var p = Base(exe);
        GdiInput(p, src, cfg);
        Add(p, "-vf", $"scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=yuv420p");
        X264(p, cfg);
        return p;
    }

    private static ProcessStartInfo BuildDdaVp8(string exe, CaptureSource src, StreamConfig cfg)
    {
        var p = Base(exe);
        DdaInput(p, src, cfg);
        Add(p, "-vf", $"hwdownload,format=bgra,scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=yuv420p");
        Vp8(p, cfg);
        return p;
    }

    private static ProcessStartInfo BuildGdiVp8(string exe, CaptureSource src, StreamConfig cfg)
    {
        var p = Base(exe);
        GdiInput(p, src, cfg);
        Add(p, "-vf", $"scale={Even(cfg.Width)}:{Even(cfg.Height)}:flags=fast_bilinear,format=yuv420p");
        Vp8(p, cfg);
        return p;
    }

    private static string Tail(string stderr) => string.Join(" | ", stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).TakeLast(10));

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try { return await task; } catch { return string.Empty; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }
}
