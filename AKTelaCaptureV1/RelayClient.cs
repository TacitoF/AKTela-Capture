using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AKTelaCapture;

internal sealed class RelayClient : IAsyncDisposable
{
    private const string RelayWs = "wss://aktela-relay.tacito1-filho.workers.dev/ws";
    private const string RelayHealth = "https://aktela-relay.tacito1-filho.workers.dev/health";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _cts; private Task? _task;
    private Channel<byte[]>? _mediaQueue;
    private Channel<byte[]>? _controlQueue;
    private string _room = string.Empty; private StreamConfig? _config; private int _viewers; private long _latency;
    public bool IsRunning => _task is { IsCompleted: false }; public int ViewerCount => Volatile.Read(ref _viewers); public long LatencyMs => Volatile.Read(ref _latency);
    public event Action<bool>? ConnectionChanged; public event Action<int>? ViewerCountChanged; public event Action<long>? LatencyChanged; public event Action<string>? Error;

    public async Task StartAsync(string room, StreamConfig config)
    {
        if (IsRunning) return;
        _room = Normalize(room);
        if (!System.Text.RegularExpressions.Regex.IsMatch(_room, "^[A-Z2-9]{6}$")) throw new ArgumentException("Código inválido. Use os 6 caracteres mostrados na Activity.");
        _config = config;
        try { using var health = await Http.GetAsync(RelayHealth); health.EnsureSuccessStatusCode(); }
        catch (Exception ex) { throw new InvalidOperationException("O relay Cloudflare não respondeu. " + ex.Message); }
        _mediaQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(6)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _controlQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource(); _task = Task.Run(() => Run(_cts.Token));
    }

    public bool QueuePacket(byte[] packet) => _mediaQueue?.Writer.TryWrite(packet) == true;
    public bool QueueControl(object obj)
    {
        try { return _controlQueue?.Writer.TryWrite(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj))) == true; }
        catch { return false; }
    }

    public async Task StopAsync()
    {
        var cts = _cts; var task = _task; _cts = null; _task = null;
        if (cts is null) return; cts.Cancel(); try { if (task is not null) await Task.WhenAny(task, Task.Delay(1200)); } catch { } cts.Dispose();
        _mediaQueue = null; _controlQueue = null;
        Interlocked.Exchange(ref _viewers, 0); Interlocked.Exchange(ref _latency, 0); ViewerCountChanged?.Invoke(0); LatencyChanged?.Invoke(0); ConnectionChanged?.Invoke(false);
    }

    private async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket(); ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15); using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                await ws.ConnectAsync(new Uri($"{RelayWs}?role=publisher&room={Uri.EscapeDataString(_room)}"), token);
                await SendConfig(ws, token); ConnectionChanged?.Invoke(true);
                var send = SendLoop(ws, linked.Token); var receive = ReceiveLoop(ws, linked.Token); var ping = PingLoop(linked.Token);
                await Task.WhenAny(send, receive, ping); linked.Cancel(); try { await Task.WhenAll(send, receive, ping); } catch { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Error?.Invoke(ex.Message); }
            finally
            {
                Interlocked.Exchange(ref _viewers, 0);
                ViewerCountChanged?.Invoke(0);
                ConnectionChanged?.Invoke(false);
            }
            if (!token.IsCancellationRequested) try { await Task.Delay(1000, token); } catch { break; }
        }
    }

    private static string AdvertisedCodec(StreamConfig c)
    {
        var level = c.Width >= 1900 && c.Fps > 30 ? "2A"
            : (c.Width >= 1280 && c.Fps > 30) || c.Width >= 1900 || c.Height >= 1000 ? "28"
            : "1F";
        return $"avc1.42E0{level}";
    }

    private async Task SendConfig(ClientWebSocket ws, CancellationToken token)
    {
        var c = _config ?? throw new InvalidOperationException("Configuração ausente.");
        var json = JsonSerializer.Serialize(new
        {
            type = "stream-config",
            protocol = 4,
            videoCodec = AdvertisedCodec(c),
            width = c.Width,
            height = c.Height,
            fps = c.Fps,
            audioEnabled = c.AudioEnabled,
            audioSampleRate = 48000,
            audioChannels = 2,
            preset = c.Preset
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token);
    }

    private async Task SendLoop(ClientWebSocket ws, CancellationToken token)
    {
        var media = _mediaQueue ?? throw new InvalidOperationException("Fila de mídia ausente.");
        var control = _controlQueue ?? throw new InvalidOperationException("Fila de controle ausente.");

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            while (control.Reader.TryRead(out var controlData))
            {
                await ws.SendAsync(controlData, WebSocketMessageType.Text, true, token);
            }

            if (media.Reader.TryRead(out var mediaData))
            {
                await ws.SendAsync(mediaData, WebSocketMessageType.Binary, true, token);
                continue;
            }

            var controlWait = control.Reader.WaitToReadAsync(token).AsTask();
            var mediaWait = media.Reader.WaitToReadAsync(token).AsTask();
            await Task.WhenAny(controlWait, mediaWait);
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream(); WebSocketReceiveResult result;
            do { result = await ws.ReceiveAsync(buffer, token); if (result.MessageType == WebSocketMessageType.Close) return; if (result.MessageType == WebSocketMessageType.Text) ms.Write(buffer,0,result.Count); } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            try
            {
                using var doc = JsonDocument.Parse(ms.ToArray()); var root = doc.RootElement; var type = root.TryGetProperty("type",out var t)?t.GetString():null;
                if (type == "viewer-count" && root.TryGetProperty("count", out var c)) { var n = Math.Max(0,c.GetInt32()); Interlocked.Exchange(ref _viewers,n); ViewerCountChanged?.Invoke(n); }
                else if (type == "pong" && root.TryGetProperty("sentAt",out var s) && s.TryGetInt64(out var sent)) { var l=Math.Max(0,Environment.TickCount64-sent); Interlocked.Exchange(ref _latency,l); LatencyChanged?.Invoke(l); }
            }
            catch { }
        }
    }
    private async Task PingLoop(CancellationToken token) { while (!token.IsCancellationRequested) { QueueControl(new { type="ping", sentAt=Environment.TickCount64 }); await Task.Delay(4000,token); } }
    public static string Normalize(string value) => new(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(6).ToArray());
    public async ValueTask DisposeAsync() => await StopAsync();
}
