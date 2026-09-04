using System.Diagnostics;
using ScreenCapture.NET;
using HPPH;

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

    public event Action<double>? FpsChanged;
    public event Action<string>? CaptureError;

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

    public Task StartAsync(Display display, int targetFps = 30)
    {
        if (_captureTask is { IsCompleted: false })
            return Task.CompletedTask;

        if (_capture is null || !ReferenceEquals(_activeDisplay, display))
        {
            _capture = _service.GetScreenCapture(display);
            _capture.Timeout = 120;
            _zone = _capture.RegisterCaptureZone(0, 0, display.Width, display.Height);
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

        if (cts is null)
            return;

        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await Task.WhenAny(task, Task.Delay(750));
            }
            catch
            {
                // Encerramento silencioso: a UI exibirá somente erros relevantes ao usuário.
            }
        }

        cts.Dispose();
        FpsChanged?.Invoke(0);
    }

    private async Task CaptureLoop(CancellationToken token, int targetFps)
    {
        if (_capture is null || _zone is null)
            return;

        var minFrameTime = TimeSpan.FromMilliseconds(1000d / Math.Clamp(targetFps, 5, 60));
        var frameClock = Stopwatch.StartNew();

        try
        {
            while (!token.IsCancellationRequested)
            {
                frameClock.Restart();

                // DX11 Desktop Duplication. O quadro permanece em memória de captura;
                // não fazemos preview nem cópias extras nesta versão para reduzir overhead.
                _capture.CaptureScreen();
                Interlocked.Increment(ref _frames);

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
            // Normal ao desligar.
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(ex.Message);
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _service.Dispose(); } catch { }
        _cts?.Dispose();
    }
}
