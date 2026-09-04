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
    private Channel<byte[]>? _frames;
    private string _roomCode = string.Empty;
    private int _viewerCount;

    public event Action<bool>? ConnectionChanged;
    public event Action<int>? ViewerCountChanged;
    public event Action<string>? RelayError;

    public bool IsRunning => _runTask is { IsCompleted: false };
    public int ViewerCount => Volatile.Read(ref _viewerCount);

    public Task StartAsync(string roomCode)
    {
        if (IsRunning) return Task.CompletedTask;

        _roomCode = NormalizeRoomCode(roomCode);
        if (_roomCode.Length != 6)
            throw new ArgumentException("Informe o código de 6 caracteres exibido na Activity.");

        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public bool TryQueueFrame(byte[] frame)
    {
        var channel = _frames;
        return channel is not null && channel.Writer.TryWrite(frame);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var task = _runTask;
        _cts = null;
        _runTask = null;

        if (cts is null) return;

        cts.Cancel();
        try
        {
            if (task is not null)
                await Task.WhenAny(task, Task.Delay(1500));
        }
        catch { }

        cts.Dispose();
        _frames = null;
        Interlocked.Exchange(ref _viewerCount, 0);
        ViewerCountChanged?.Invoke(0);
        ConnectionChanged?.Invoke(false);
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);

            try
            {
                var uri = new Uri($"{RelayBaseUrl}?role=publisher&room={Uri.EscapeDataString(_roomCode)}");
                await socket.ConnectAsync(uri, token);
                ConnectionChanged?.Invoke(true);

                var sender = SendLoopAsync(socket, linked.Token);
                var receiver = ReceiveLoopAsync(socket, linked.Token);
                var heartbeat = HeartbeatLoopAsync(socket, linked.Token);

                await Task.WhenAny(sender, receiver, heartbeat);
                linked.Cancel();

                try { await Task.WhenAll(sender, receiver, heartbeat); } catch { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RelayError?.Invoke(ex.Message);
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
            }

            if (!token.IsCancellationRequested)
            {
                try { await Task.Delay(1200, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var channel = _frames ?? throw new InvalidOperationException("Fila de frames não inicializada.");

        while (await channel.Reader.WaitToReadAsync(token))
        {
            byte[]? latest = null;
            while (channel.Reader.TryRead(out var frame))
                latest = frame;

            if (latest is null) continue;
            if (socket.State != WebSocketState.Open) return;

            await socket.SendAsync(latest, WebSocketMessageType.Binary, true, token);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            try
            {
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                if (!root.TryGetProperty("type", out var typeElement)) continue;

                var type = typeElement.GetString();
                if (type == "viewer-count" && root.TryGetProperty("count", out var countElement))
                {
                    var count = Math.Max(0, countElement.GetInt32());
                    Interlocked.Exchange(ref _viewerCount, count);
                    ViewerCountChanged?.Invoke(count);
                }
                else if (type == "error" && root.TryGetProperty("message", out var messageElement))
                {
                    RelayError?.Invoke(messageElement.GetString() ?? "Erro no servidor de transmissão.");
                }
            }
            catch
            {
                // Mensagens de controle inválidas não derrubam a transmissão.
            }
        }
    }

    private static async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            if (socket.State != WebSocketState.Open) return;
            var payload = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, token);
        }
    }

    public static string NormalizeRoomCode(string value)
    {
        var chars = value
            .Trim()
            .ToUpperInvariant()
            .Where(c => char.IsLetterOrDigit(c))
            .Take(6)
            .ToArray();
        return new string(chars);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
