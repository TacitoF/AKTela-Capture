using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AKTelaCapture;

internal sealed class AudioStreamer : IAsyncDisposable
{
    private const int Rate = 48_000, Channels = 2, FrameSamples = 960, FrameBytes = FrameSamples * Channels * 2;
    private const int FrameDurationUs = 20_000;
    private const int MaxBufferedFrames = 6;
    private WasapiRecorder? _recorder; private BufferedWaveProvider? _buffer; private CancellationTokenSource? _cts; private Task? _task;
    private readonly FixedFrameTimestampClock _timestampClock = new();
    private readonly SemaphoreSlim _dataReady = new(0, 1);
    public bool IsRunning => _task is { IsCompleted: false };
    public event Action<byte[]>? PacketReady; public event Action<string>? Error;

    public async Task StartAsync(AudioMode mode, int sourcePid)
    {
        if (mode == AudioMode.Off || _task is { IsCompleted: false }) return;
        try
        {
            _timestampClock.Reset();
            while (_dataReady.Wait(0)) { }
            var format = new WaveFormat(Rate, 16, Channels);
            var builder = new WasapiRecorderBuilder().WithFormat(format).WithBufferLength(40);
            if (mode == AudioMode.SourceOnly)
            {
                var root = ProcessTreeHelper.FindApplicationRootProcessId(sourcePid);
                _recorder = await builder.WithProcessLoopback((uint)root, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync();
            }
            else
            {
                var discord = ProcessTreeHelper.FindDiscordRootProcessId() ?? throw new InvalidOperationException("Discord não encontrado para exclusão do áudio.");
                _recorder = await builder.WithProcessLoopback((uint)discord, ProcessLoopbackMode.ExcludeTargetProcessTree).BuildAsync();
            }
            _buffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
            _recorder.DataAvailable += OnData;
            _recorder.StartRecording();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // O áudio não pode depender de uma thread do pool que o jogo consiga
            // deixar sem tempo de CPU. A thread é dedicada e faz pouco trabalho:
            // acorda a cada bloco de 20 ms e usa Opus de baixa complexidade.
            _task = Task.Factory.StartNew(
                () => EncodeLoop(token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch (Exception ex) { await StopAsync(); Error?.Invoke(ex.Message); }
    }

    public async Task StopAsync()
    {
        var cts = _cts; var task = _task; _cts = null; _task = null;
        if (cts is not null) { cts.Cancel(); try { if (task is not null) await Task.WhenAny(task, Task.Delay(600)); } catch { } cts.Dispose(); }
        if (_recorder is not null) { try { _recorder.DataAvailable -= OnData; _recorder.StopRecording(); await _recorder.DisposeAsync(); } catch { } _recorder = null; }
        _buffer = null;
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            if (data.Length <= 0) return;
            _buffer?.AddSamples(data);
            try { _dataReady.Release(); } catch (SemaphoreFullException) { }
        }
        catch { }
    }
    private void EncodeLoop(CancellationToken token)
    {
        if (_buffer is null) return;
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
        using var encoder = OpusCodecFactory.CreateEncoder(Rate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = 128_000;
        encoder.Complexity = 3;
        encoder.UseVBR = true;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        var pcmBytes = new byte[FrameBytes]; var discarded = new byte[FrameBytes];
        var pcm = new short[FrameSamples * Channels]; var encoded = new byte[4000];
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_buffer.BufferedBytes < FrameBytes) { _dataReady.Wait(token); continue; }

                // Se o encoder perder tempo de CPU, mantenha somente os 120 ms mais
                // recentes. Enviar todo o áudio antigo faria a voz ficar atrás do vídeo.
                var skipped = false;
                while (_buffer.BufferedBytes > FrameBytes * MaxBufferedFrames)
                {
                    _buffer.Read(discarded.AsSpan(0, FrameBytes));
                    skipped = true;
                }
                if (skipped) _timestampClock.Reset();

                var read = _buffer.Read(pcmBytes.AsSpan(0, FrameBytes)); if (read < FrameBytes) continue;
                Buffer.BlockCopy(pcmBytes, 0, pcm, 0, FrameBytes);
                var count = encoder.Encode(pcm.AsSpan(), FrameSamples, encoded.AsSpan(), encoded.Length); if (count <= 0) continue;
                var timestampUs = _timestampClock.Next(MediaClock.NowMicroseconds(), FrameDurationUs);
                PacketReady?.Invoke(PacketProtocol.Create(MediaKind.Audio, true, timestampUs, FrameDurationUs, encoded.AsSpan(0, count)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _dataReady.Dispose();
    }
}
