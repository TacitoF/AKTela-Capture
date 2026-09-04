using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AKTelaCapture;

internal sealed class RelayClient : IAsyncDisposable
{
    private const string RelayBaseUrl = "wss://ak-tela-three.vercel.app/api/ws";
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Channel<OutgoingMessage>? _outgoing;
    private string _roomCode = string.Empty;
    private StreamConfig? _config;
    private int _viewerCount;
    private long _latencyMs;

    public event Action<bool>? ConnectionChanged;
    public event Action<int>? ViewerCountChanged;
    public event Action<long>? LatencyChanged;
    public event Action<string>? RelayError;
    public bool IsRunning => _runTask is { IsCompleted: false };
    public int ViewerCount => Volatile.Read(ref _viewerCount);
    public long LatencyMs => Volatile.Read(ref _latencyMs);

    public Task StartAsync(string roomCode, StreamConfig config)
    {
        if (IsRunning) return Task.CompletedTask;
        _roomCode = NormalizeRoomCode(roomCode);
        if (_roomCode.Length != 6) throw new ArgumentException("Informe o código de 6 caracteres exibido na Activity.");
        _config = config;
        _outgoing = Channel.CreateBounded<OutgoingMessage>(new BoundedChannelOptions(24)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public bool TryQueuePacket(byte[] packet) => _outgoing?.Writer.TryWrite(new OutgoingMessage(packet, WebSocketMessageType.Binary)) == true;

    public bool TryQueueControl(object payload)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            return _outgoing?.Writer.TryWrite(new OutgoingMessage(bytes, WebSocketMessageType.Text)) == true;
        }
        catch { return false; }
    }

    public async Task StopAsync()
    {
        var cts = _cts; var task = _runTask;
        _cts = null; _runTask = null;
        if (cts is null) return;
        cts.Cancel();
        try { if (task is not null) await Task.WhenAny(task, Task.Delay(1500)); } catch { }
        cts.Dispose(); _outgoing = null;
        Interlocked.Exchange(ref _viewerCount, 0);
        Interlocked.Exchange(ref _latencyMs, 0);
        ViewerCountChanged?.Invoke(0); LatencyChanged?.Invoke(0); ConnectionChanged?.Invoke(false);
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                await socket.ConnectAsync(new Uri($"{RelayBaseUrl}?role=publisher&room={Uri.EscapeDataString(_roomCode)}"), token);
                await SendConfigAsync(socket, token);
                ConnectionChanged?.Invoke(true);
                var sender = SendLoopAsync(socket, linked.Token);
                var receiver = ReceiveLoopAsync(socket, linked.Token);
                var heartbeat = HeartbeatLoopAsync(linked.Token);
                await Task.WhenAny(sender, receiver, heartbeat);
                linked.Cancel();
                try { await Task.WhenAll(sender, receiver, heartbeat); } catch { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { RelayError?.Invoke(ex.Message); }
            finally { ConnectionChanged?.Invoke(false); }
            if (!token.IsCancellationRequested) try { await Task.Delay(900, token); } catch { break; }
        }
    }

    private async Task SendConfigAsync(ClientWebSocket socket, CancellationToken token)
    {
        var c = _config ?? new StreamConfig(1920, 1080, 30, 8, true, "Personalizado", "Tela", "Sistema", "Auto");
        var json = JsonSerializer.Serialize(new
        {
            type = "stream-config",
            videoCodec = "avc1.64002A",
            width = c.Width,
            height = c.Height,
            fps = c.Fps,
            videoBitrateMbps = c.VideoBitrateMbps,
            audioEnabled = c.AudioEnabled,
            audioCodec = "opus",
            audioSampleRate = 48000,
            audioChannels = 2,
            preset = c.PresetName,
            sourceKind = c.SourceKind,
            audioMode = c.AudioMode,
            cursorPolicy = c.CursorPolicy
        });
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token);
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var channel = _outgoing ?? throw new InvalidOperationException("Fila de saída não inicializada.");
        while (await channel.Reader.WaitToReadAsync(token))
        {
            while (channel.Reader.TryRead(out var message))
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(message.Data, message.Type, true, token);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var ms = new MemoryStream(); WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType == WebSocketMessageType.Text) ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            try
            {
                using var json = JsonDocument.Parse(ms.ToArray());
                var root = json.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "viewer-count" && root.TryGetProperty("count", out var ce))
                {
                    var count = Math.Max(0, ce.GetInt32());
                    Interlocked.Exchange(ref _viewerCount, count);
                    ViewerCountChanged?.Invoke(count);
                }
                else if (type == "pong" && root.TryGetProperty("sentAt", out var se) && se.TryGetInt64(out var sentAt))
                {
                    var latency = Math.Max(0, Environment.TickCount64 - sentAt);
                    Interlocked.Exchange(ref _latencyMs, latency);
                    LatencyChanged?.Invoke(latency);
                }
                else if (type == "error" && root.TryGetProperty("message", out var me))
                {
                    RelayError?.Invoke(me.GetString() ?? "Erro no relay.");
                }
            }
            catch { }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TryQueueControl(new { type = "ping", sentAt = Environment.TickCount64 });
            await Task.Delay(TimeSpan.FromSeconds(4), token);
        }
    }

    public static string NormalizeRoomCode(string value) => new(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(6).ToArray());
    public async ValueTask DisposeAsync() => await StopAsync();

    private readonly record struct OutgoingMessage(byte[] Data, WebSocketMessageType Type);
}
