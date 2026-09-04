using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AKTelaCapture;

internal sealed class AudioStreamer : IAsyncDisposable
{
    private const int Rate = 48_000, Channels = 2, FrameSamples = 960, FrameBytes = FrameSamples * Channels * 2;
    private WasapiRecorder? _recorder; private BufferedWaveProvider? _buffer; private CancellationTokenSource? _cts; private Task? _task;
    public event Action<byte[]>? PacketReady; public event Action<string>? Error;

    public async Task StartAsync(AudioMode mode, int sourcePid)
    {
        if (mode == AudioMode.Off || _task is { IsCompleted: false }) return;
        try
        {
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
            _task = Task.Run(() => EncodeLoop(_cts.Token));
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

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition) { try { if (data.Length > 0) _buffer?.AddSamples(data); } catch { } }
    private async Task EncodeLoop(CancellationToken token)
    {
        if (_buffer is null) return;
        var encoder = new OpusEncoder(Rate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY) { Bitrate = 128_000, Complexity = 3, UseVBR = true, SignalType = OpusSignal.OPUS_SIGNAL_MUSIC };
        var pcmBytes = new byte[FrameBytes]; var pcm = new short[FrameSamples * Channels]; var encoded = new byte[4000];
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_buffer.BufferedBytes < FrameBytes) { await Task.Delay(3, token); continue; }
                var read = _buffer.Read(pcmBytes.AsSpan(0, FrameBytes)); if (read < FrameBytes) continue;
                Buffer.BlockCopy(pcmBytes, 0, pcm, 0, FrameBytes);
                var count = encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length); if (count <= 0) continue;
                PacketReady?.Invoke(PacketProtocol.Create(MediaKind.Audio, true, MediaClock.NowMicroseconds(), 20_000, encoded.AsSpan(0, count)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
