using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;

namespace AKTelaCapture;

internal sealed class AudioStreamer : IAsyncDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int FrameSamples = 960; // 20 ms
    private const int FrameBytes = FrameSamples * Channels * sizeof(short);

    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private MediaFoundationResampler? _resampler;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public event Action<byte[]>? PacketReady;
    public event Action<string>? AudioError;

    public Task StartAsync(CancellationToken token = default)
    {
        if (_task is { IsCompleted: false }) return Task.CompletedTask;

        try
        {
            _capture = new WasapiLoopbackCapture();
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true
            };
            _resampler = new MediaFoundationResampler(_buffer, new WaveFormat(SampleRate, 16, Channels))
            {
                ResamplerQuality = 30
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _task = Task.Run(() => EncodeLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Cleanup();
            AudioError?.Invoke(ex.Message);
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var task = _task;
        _cts = null;
        _task = null;
        if (cts is not null)
        {
            cts.Cancel();
            if (task is not null)
            {
                try { await Task.WhenAny(task, Task.Delay(800)); } catch { }
            }
            cts.Dispose();
        }
        Cleanup();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try { _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
    }

    private async Task EncodeLoopAsync(CancellationToken token)
    {
        if (_resampler is null) return;

        try
        {
            var encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY)
            {
                Bitrate = 128_000,
                Complexity = 4,
                UseVBR = true,
                SignalType = OpusSignal.OPUS_SIGNAL_MUSIC
            };

            var pcmBytes = new byte[FrameBytes];
            var pcm = new short[FrameSamples * Channels];
            var encoded = new byte[4000];

            while (!token.IsCancellationRequested)
            {
                var total = 0;
                while (total < FrameBytes && !token.IsCancellationRequested)
                {
                    var read = _resampler.Read(pcmBytes, total, FrameBytes - total);
                    if (read <= 0)
                    {
                        await Task.Delay(4, token);
                        continue;
                    }
                    total += read;
                }

                if (total < FrameBytes) continue;
                Buffer.BlockCopy(pcmBytes, 0, pcm, 0, FrameBytes);
                var count = encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
                if (count <= 0) continue;

                var packet = MediaPacket.Create(
                    MediaKind.Audio,
                    true,
                    MediaClock.NowMicroseconds(),
                    20_000,
                    encoded.AsSpan(0, count));
                PacketReady?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AudioError?.Invoke(ex.Message);
        }
    }

    private void Cleanup()
    {
        if (_capture is not null)
        {
            try { _capture.DataAvailable -= OnDataAvailable; } catch { }
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        try { _resampler?.Dispose(); } catch { }
        _resampler = null;
        _buffer = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
