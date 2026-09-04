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
    private Channel<byte[]>? _packets;
    private string _roomCode = string.Empty;
    private StreamConfig? _config;
    private int _viewerCount;

    public event Action<bool>? ConnectionChanged;
    public event Action<int>? ViewerCountChanged;
    public event Action<string>? RelayError;
    public bool IsRunning => _runTask is { IsCompleted: false };
    public int ViewerCount => Volatile.Read(ref _viewerCount);

    public Task StartAsync(string roomCode, StreamConfig config)
    {
        if (IsRunning) return Task.CompletedTask;
        _roomCode = NormalizeRoomCode(roomCode);
        if (_roomCode.Length != 6) throw new ArgumentException("Informe o código de 6 caracteres exibido na Activity.");
        _config = config;
        _packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(96)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public bool TryQueuePacket(byte[] packet) => _packets?.Writer.TryWrite(packet) == true;

    public async Task StopAsync()
    {
        var cts = _cts; var task = _runTask;
        _cts = null; _runTask = null;
        if (cts is null) return;
        cts.Cancel();
        try { if (task is not null) await Task.WhenAny(task, Task.Delay(1500)); } catch { }
        cts.Dispose(); _packets = null;
        Interlocked.Exchange(ref _viewerCount, 0);
        ViewerCountChanged?.Invoke(0); ConnectionChanged?.Invoke(false);
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
                var heartbeat = HeartbeatLoopAsync(socket, linked.Token);
                await Task.WhenAny(sender, receiver, heartbeat);
                linked.Cancel();
                try { await Task.WhenAll(sender, receiver, heartbeat); } catch { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { RelayError?.Invoke(ex.Message); }
            finally { ConnectionChanged?.Invoke(false); }
            if (!token.IsCancellationRequested) try { await Task.Delay(1000, token); } catch { break; }
        }
    }

    private async Task SendConfigAsync(ClientWebSocket socket, CancellationToken token)
    {
        var c = _config ?? new StreamConfig(30, 8, true);
        var json = JsonSerializer.Serialize(new
        {
            type = "stream-config", videoCodec = "avc1.64002A", width = c.Width, height = c.Height,
            fps = c.Fps, videoBitrateMbps = c.VideoBitrateMbps,
            audioEnabled = c.AudioEnabled, audioCodec = "opus", audioSampleRate = 48000, audioChannels = 2
        });
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token);
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var channel = _packets ?? throw new InvalidOperationException("Fila de mídia não inicializada.");
        while (await channel.Reader.WaitToReadAsync(token))
        {
            while (channel.Reader.TryRead(out var packet))
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(packet, WebSocketMessageType.Binary, true, token);
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
                    var count = Math.Max(0, ce.GetInt32()); Interlocked.Exchange(ref _viewerCount, count); ViewerCountChanged?.Invoke(count);
                }
                else if (type == "error" && root.TryGetProperty("message", out var me)) RelayError?.Invoke(me.GetString() ?? "Erro no relay.");
            }
            catch { }
        }
    }

    private static async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            if (socket.State == WebSocketState.Open) await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"), WebSocketMessageType.Text, true, token);
        }
    }

    public static string NormalizeRoomCode(string value) => new(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(6).ToArray());
    public async ValueTask DisposeAsync() => await StopAsync();
}
