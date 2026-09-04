using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AKTelaCapture;

internal sealed class AudioStreamer : IAsyncDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int FrameSamples = 960;
    private const int FrameBytes = FrameSamples * Channels * sizeof(short);

    private WasapiRecorder? _recorder;
    private BufferedWaveProvider? _buffer;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public event Action<byte[]>? PacketReady;
    public event Action<string>? AudioError;
    public event Action<string>? AudioModeChanged;

    public async Task StartAsync(AudioCaptureMode mode, int sourceProcessId = 0, CancellationToken token = default)
    {
        if (_task is { IsCompleted: false } || mode == AudioCaptureMode.Off) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            AudioError?.Invoke("O áudio por aplicativo requer Windows 10 2004 ou superior.");
            return;
        }

        try
        {
            var format = new WaveFormat(SampleRate, 16, Channels);
            var builder = new WasapiRecorderBuilder().WithFormat(format).WithBufferLength(40);

            switch (mode)
            {
                case AudioCaptureMode.SourceOnly:
                    if (sourceProcessId <= 0) throw new InvalidOperationException("Selecione uma janela ou jogo para capturar somente o áudio da fonte.");
                    var root = ProcessTreeHelper.FindApplicationRootProcessId(sourceProcessId);
                    _recorder = await builder
                        .WithProcessLoopback((uint)root, ProcessLoopbackMode.IncludeTargetProcessTree)
                        .BuildAsync();
                    AudioModeChanged?.Invoke("Somente da fonte");
                    break;

                case AudioCaptureMode.SystemWithoutDiscord:
                    var discordPid = ProcessTreeHelper.FindDiscordRootProcessId();
                    if (discordPid is null)
                        throw new InvalidOperationException("Não encontrei o processo do Discord para excluí-lo do áudio. Abra o Discord ou escolha outro modo de áudio.");
                    _recorder = await builder
                        .WithProcessLoopback((uint)discordPid.Value, ProcessLoopbackMode.ExcludeTargetProcessTree)
                        .BuildAsync();
                    AudioModeChanged?.Invoke("Sistema sem Discord");
                    break;

                case AudioCaptureMode.SystemAll:
                    _recorder = builder.WithLoopbackCapture().Build();
                    AudioModeChanged?.Invoke("Sistema inteiro");
                    break;
            }

            if (_recorder is null) return;
            _buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(220),
                DiscardOnBufferOverflow = true
            };
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.StartRecording();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _task = Task.Run(() => EncodeLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            AudioError?.Invoke(ex.Message);
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
        await CleanupAsync();
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            if (buffer.Length > 0) _buffer?.AddSamples(buffer);
        }
        catch { }
    }

    private async Task EncodeLoopAsync(CancellationToken token)
    {
        if (_buffer is null) return;
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
                if (_buffer.BufferedBytes < FrameBytes)
                {
                    await Task.Delay(3, token);
                    continue;
                }

                var total = _buffer.Read(pcmBytes.AsSpan(0, FrameBytes));
                if (total < FrameBytes) continue;
                Buffer.BlockCopy(pcmBytes, 0, pcm, 0, FrameBytes);
                var count = encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
                if (count <= 0) continue;

                PacketReady?.Invoke(MediaPacket.Create(
                    MediaKind.Audio,
                    true,
                    MediaClock.NowMicroseconds(),
                    20_000,
                    encoded.AsSpan(0, count)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AudioError?.Invoke(ex.Message); }
    }

    private async Task CleanupAsync()
    {
        if (_recorder is not null)
        {
            try { _recorder.DataAvailable -= OnDataAvailable; } catch { }
            try { _recorder.StopRecording(); } catch { }
            try { await _recorder.DisposeAsync(); } catch { }
            _recorder = null;
        }
        _buffer = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
