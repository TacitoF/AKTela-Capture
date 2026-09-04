using System.Diagnostics;
using System.Drawing.Imaging;
using HPPH;
using ScreenCapture.NET;
using Encoder = System.Drawing.Imaging.Encoder;

namespace AKTelaCapture;

internal sealed class CaptureController : IDisposable
{
    private readonly DX11ScreenCaptureService _service = new();
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private DX11ScreenCapture? _capture;
    private CaptureZone<ColorBGRA>? _zone;
    private Display? _activeDisplay;
    private long _frames;
    private readonly Stopwatch _fpsClock = new();
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo
        .GetImageEncoders()
        .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    public event Action<double>? FpsChanged;
    public event Action<byte[]>? FrameReady;
    public event Action<string>? CaptureError;

    /// <summary>
    /// Quando não há espectadores, pulamos a compactação JPEG para reduzir uso de CPU.
    /// </summary>
    public Func<bool>? ShouldEncodeFrame { get; set; }

    public IReadOnlyList<DisplayOption> GetDisplays()
    {
        var result = new List<DisplayOption>();
        var number = 1;

        foreach (var graphicsCard in _service.GetGraphicsCards())
        {
            foreach (var display in _service.GetDisplays(graphicsCard))
            {
                result.Add(new DisplayOption(display, number++));
            }
        }

        return result;
    }

    public Task StartAsync(Display display, int targetFps = 15)
    {
        if (_captureTask is { IsCompleted: false })
            return Task.CompletedTask;

        if (_capture is null || !_activeDisplay.Equals(display))
        {
            try { _capture?.Dispose(); } catch { }
            _capture = _service.GetScreenCapture(display);
            _capture.Timeout = 16;

            // Downscale nativo do pipeline para reduzir cópia/encode/banda.
            // 1920x1080 vira 960x540, suficiente para validar o streaming com baixo impacto.
            var downscaleLevel = display.Width >= 1280 && display.Height >= 720 ? 1 : 0;
            _zone = _capture.RegisterCaptureZone(0, 0, display.Width, display.Height, downscaleLevel);
            _activeDisplay = display;
        }

        _cts = new CancellationTokenSource();
        _frames = 0;
        _fpsClock.Restart();
        _captureTask = Task.Run(() => CaptureLoop(_cts.Token, targetFps));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var task = _captureTask;
        _cts = null;
        _captureTask = null;

        if (cts is null) return;

        cts.Cancel();
        if (task is not null)
        {
            try { await Task.WhenAny(task, Task.Delay(1000)); }
            catch { }
        }

        cts.Dispose();
        FpsChanged?.Invoke(0);
    }

    private async Task CaptureLoop(CancellationToken token, int targetFps)
    {
        if (_capture is null || _zone is null) return;

        var minFrameTime = TimeSpan.FromMilliseconds(1000d / Math.Clamp(targetFps, 5, 30));
        var frameClock = Stopwatch.StartNew();

        try
        {
            while (!token.IsCancellationRequested)
            {
                frameClock.Restart();
                var captured = _capture.CaptureScreen();

                if (captured)
                {
                    Interlocked.Increment(ref _frames);

                    if (ShouldEncodeFrame?.Invoke() ?? true)
                    {
                        var jpeg = EncodeJpeg(_zone, quality: 52L);
                        if (jpeg.Length > 0)
                            FrameReady?.Invoke(jpeg);
                    }
                }

                if (_fpsClock.ElapsedMilliseconds >= 1000)
                {
                    var elapsed = Math.Max(_fpsClock.Elapsed.TotalSeconds, 0.001);
                    var count = Interlocked.Exchange(ref _frames, 0);
                    FpsChanged?.Invoke(count / elapsed);
                    _fpsClock.Restart();
                }

                var remaining = minFrameTime - frameClock.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal.
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(ex.Message);
        }
    }

    private static unsafe byte[] EncodeJpeg(CaptureZone<ColorBGRA> zone, long quality)
    {
        using var zoneLock = zone.Lock();
        var raw = zone.RawBuffer;
        if (raw.IsEmpty) return [];

        fixed (byte* pointer = raw)
        {
            using var bitmap = new Bitmap(
                zone.Width,
                zone.Height,
                zone.Width * 4,
                PixelFormat.Format32bppArgb,
                (IntPtr)pointer);

            using var stream = new MemoryStream(capacity: 128 * 1024);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 25L, 85L));
            bitmap.Save(stream, JpegCodec, parameters);
            return stream.ToArray();
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _service.Dispose(); } catch { }
        _cts?.Dispose();
    }
}
