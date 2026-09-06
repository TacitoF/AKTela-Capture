using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AKTelaCapture;

internal sealed class RelayClient : IAsyncDisposable
{
    private const string RelayWs = "wss://aktela-relay.tacito1-filho.workers.dev/ws";
    private const string RelayHealth = "https://aktela-relay.tacito1-filho.workers.dev/health";
    // Capacidade 2 (~66ms a 30fps) descartava a fila inteira a qualquer micro-oscilação
    // da rede, forçando espera por um novo quadro-chave (até 1s) e travando a imagem de
    // quem assistia. Um pouco mais de folga absorve jitter sem custar latência perceptível.
    private const int VideoCapacity = 5;
    private const int AudioCapacity = 24;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private CancellationTokenSource? _cts;
    private Task? _task;
    private VideoPacketQueue? _videoQueue;
    private Channel<byte[]>? _audioQueue;
    private Channel<byte[]>? _controlQueue;
    private TaskCompletionSource<bool>? _firstHandshake;

    private string _room = string.Empty;
    private string _publisherId = string.Empty;
    private StreamConfig? _config;
    private int _viewers;
    private long _latency;
    private long _viewerLatency;
    private long _lastPongAt;
    private int _reconnects;
    private bool _publisherRejected;
    private string _lastError = string.Empty;

    private long _videoSent;
    private long _audioSent;
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private long _audioDropped;
    private long _lastPingSentAt;

    public bool IsRunning => _task is { IsCompleted: false };
    public bool IsConnected { get; private set; }
    public int ViewerCount => Volatile.Read(ref _viewers);
    public long LatencyMs
    {
        get
        {
            var viewer = Volatile.Read(ref _viewerLatency);
            return ViewerCount > 0 && viewer > 0 ? viewer : Volatile.Read(ref _latency);
        }
    }
    public long VideoDropped => (_videoQueue?.Dropped ?? 0);

    public event Action<bool>? ConnectionChanged;
    public event Action<int>? ViewerCountChanged;
    public event Action<long>? LatencyChanged;
    public event Action<AudienceCapabilities>? AudienceCapabilitiesChanged;
    public event Action? KeyframeRequested;
    public event Action<string>? PublisherRejected;
    public event Action<string>? Error;
    public event Action<RelayDiagnostics>? DiagnosticsChanged;
    // Disparado a cada quadro delta descartado por congestionamento, para permitir reação
    // imediata (reduzir qualidade) em vez de esperar o próximo ciclo do timer periódico.
    public event Action? VideoCongested;

    public static async Task CheckHealthAsync(CancellationToken token = default)
    {
        using var response = await Http.GetAsync(RelayHealth, token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(token);
        if (!text.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O relay respondeu, mas o health check não retornou OK.");
    }

    public async Task StartAsync(string room, StreamConfig config)
    {
        if (IsRunning) return;

        _room = Normalize(room);
        if (!IsValidCode(_room))
            throw new ArgumentException("Código inválido. Use os 6 caracteres mostrados na Activity.");

        await CheckHealthAsync();

        _config = config;
        _publisherId = Guid.NewGuid().ToString("N");
        _publisherRejected = false;
        _lastError = string.Empty;
        _reconnects = 0;
        Interlocked.Exchange(ref _videoSent, 0);
        Interlocked.Exchange(ref _audioSent, 0);
        Interlocked.Exchange(ref _lastPingSentAt, 0);

        Interlocked.Exchange(ref _audioDropped, 0);

        _videoQueue = new VideoPacketQueue(VideoCapacity);

        _audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AudioCapacity)
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

        _firstHandshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Run(() => Run(token));

        var completed = await Task.WhenAny(_firstHandshake.Task, Task.Delay(7000));
        if (completed != _firstHandshake.Task)
        {
            await StopAsync();
            throw new TimeoutException("O relay não confirmou a transmissão em 7 segundos.");
        }

        try
        {
            await _firstHandshake.Task;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public void UpdateStreamConfig(StreamConfig config)
    {
        _config = config;
        QueueStreamConfig();
    }

    public bool QueuePacket(byte[] packet)
    {
        if (packet.Length <= 6) return false;
        return PacketProtocol.Kind(packet) == MediaKind.Audio ? QueueAudio(packet) : QueueVideo(packet);
    }

    private bool QueueVideo(byte[] packet)
    {
        var queue = _videoQueue;
        if (queue is null || !IsConnected) return false;
        var ok = queue.TryWrite(packet);
        if (ok) SignalSender();
        else VideoCongested?.Invoke();
        PublishDiagnostics();
        return ok;
    }

    private void SignalSender()
    {
        try { _sendSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private bool QueueAudio(byte[] packet)
    {
        var queue = _audioQueue;
        if (queue is null || !IsConnected) return false;
        if (QueueCount(queue) >= AudioCapacity) Interlocked.Increment(ref _audioDropped);
        var ok = queue.Writer.TryWrite(packet);
        if (ok) SignalSender();
        return ok;
    }

    public bool QueueControl(object obj)
    {
        try
        {
            return QueueControlText(JsonSerializer.Serialize(obj));
        }
        catch
        {
            return false;
        }
    }

    private bool QueueControlText(string text)
    {
        try
        {
            var ok = _controlQueue?.Writer.TryWrite(Encoding.UTF8.GetBytes(text)) == true;
            if (ok) SignalSender();
            return ok;
        }
        catch { return false; }
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
            try { if (task is not null) await task; } catch { }
            cts.Dispose();
        }

        _videoQueue = null;
        _audioQueue = null;
        _controlQueue = null;
        _firstHandshake = null;
        _publisherRejected = false;
        IsConnected = false;
        Interlocked.Exchange(ref _viewers, 0);
        Interlocked.Exchange(ref _latency, 0);
        Interlocked.Exchange(ref _viewerLatency, 0);
        ViewerCountChanged?.Invoke(0);
        LatencyChanged?.Invoke(0);
        ConnectionChanged?.Invoke(false);
        PublishDiagnostics();
    }

    private async Task Run(CancellationToken token)
    {
        var attempt = 0;

        while (!token.IsCancellationRequested && !_publisherRejected)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(12);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);

            try
            {
                _videoQueue?.Reset();
                while (_audioQueue?.Reader.TryRead(out _) == true) { }
                while (_controlQueue?.Reader.TryRead(out _) == true) { }
                await ws.ConnectAsync(new Uri($"{RelayWs}?role=publisher&room={Uri.EscapeDataString(_room)}&publisherId={Uri.EscapeDataString(_publisherId)}"), token);
                _lastPongAt = Environment.TickCount64;

                var send = SendLoop(ws, linked.Token);
                var receive = ReceiveLoop(ws, linked.Token);
                var ping = PingLoop(ws, linked.Token);

                await Task.WhenAny(send, receive, ping);
                linked.Cancel();
                try { await Task.WhenAll(send, receive, ping); } catch { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _firstHandshake?.TrySetException(new InvalidOperationException("Falha ao conectar ao relay: " + ex.Message, ex));
                Error?.Invoke(ex.Message);
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    ConnectionChanged?.Invoke(false);
                }
                Interlocked.Exchange(ref _viewers, 0);
                ViewerCountChanged?.Invoke(0);
                PublishDiagnostics();
            }

            if (token.IsCancellationRequested || _publisherRejected) break;

            attempt++;
            _reconnects++;
            PublishDiagnostics();
            var baseDelay = Math.Min(10_000, 700 * (1 << Math.Min(attempt - 1, 4)));
            var delay = baseDelay + Random.Shared.Next(0, 350);
            try { await Task.Delay(delay, token); } catch { break; }
        }
    }

    private void QueueStreamConfig()
    {
        var c = _config;
        if (c is null) return;

        QueueControl(new
        {
            type = "stream-config",
            protocol = 5,
            qualityKey = c.QualityKey,
            videoCodec = c.VideoCodec,
            videoProfile = c.VideoProfile,
            videoCodecString = c.ExpectedCodec,
            width = c.Width,
            height = c.Height,
            fps = c.Fps,
            bitrateMbps = c.BitrateMbps,
            audioEnabled = c.AudioEnabled,
            audioSampleRate = 48000,
            audioChannels = 2,
            preset = c.Preset,
            compatibilityMode = c.CompatibilityMode
        });
    }

    private async Task SendLoop(ClientWebSocket ws, CancellationToken token)
    {
        var video = _videoQueue ?? throw new InvalidOperationException("Fila de vídeo ausente.");
        var audio = _audioQueue ?? throw new InvalidOperationException("Fila de áudio ausente.");
        var control = _controlQueue ?? throw new InvalidOperationException("Fila de controle ausente.");

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var sentSomething = false;

            // Controle continua prioritário, mas com limite por ciclo para cursor/pings
            // não impedirem que os quadros de vídeo avancem.
            for (var i = 0; i < 8 && control.Reader.TryRead(out var controlData); i++)
            {
                await ws.SendAsync(controlData, WebSocketMessageType.Text, true, token);
                sentSomething = true;
            }

            // Vídeo primeiro: em compartilhamento de tela, um quadro atual é mais útil
            // do que deixar a fila acumular atraso.
            if (video.TryRead(out var videoData))
            {
                await ws.SendAsync(videoData, WebSocketMessageType.Binary, true, token);
                Interlocked.Increment(ref _videoSent);
                if ((Interlocked.Read(ref _videoSent) & 31) == 0) PublishDiagnostics();
                sentSomething = true;
            }

            // Ainda enviamos um pacote de áudio por ciclo para evitar starvation.
            if (audio.Reader.TryRead(out var audioData))
            {
                await ws.SendAsync(audioData, WebSocketMessageType.Binary, true, token);
                Interlocked.Increment(ref _audioSent);
                sentSomething = true;
            }

            if (sentSomething) continue;

            // A single bounded wake-up avoids accumulating abandoned channel waiters.
            await _sendSignal.WaitAsync(token);
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType == WebSocketMessageType.Text) ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            var textMessage = Encoding.UTF8.GetString(ms.ToArray());
            if (textMessage == "pong")
            {
                _lastPongAt = Environment.TickCount64;
                // Este "pong" vem do auto-response do Worker (setWebSocketAutoResponse),
                // respondido na borda sem acordar o Durable Object nem esperar atrás de
                // vídeo/controle na fila de mensagens. É a medida mais fiel do RTT real
                // com o relay, sem o viés de congestionamento que inflava o ping exibido
                // especificamente durante transmissões ativas.
                var sentAt = Interlocked.Read(ref _lastPingSentAt);
                if (sentAt > 0)
                {
                    var rtt = Math.Max(0, Environment.TickCount64 - sentAt);
                    Interlocked.Exchange(ref _latency, rtt);
                    LatencyChanged?.Invoke(rtt);
                    PublishDiagnostics();
                }
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(textMessage);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                switch (type)
                {
                    case "publisher-accepted":
                        _lastPongAt = Environment.TickCount64;
                        if (!IsConnected)
                        {
                            IsConnected = true;
                            ConnectionChanged?.Invoke(true);
                        }
                        _firstHandshake?.TrySetResult(true);
                        QueueStreamConfig();
                        break;

                    case "publisher-rejected":
                    {
                        var message = root.TryGetProperty("message", out var m) ? m.GetString() : "Já existe uma transmissão ativa nesta Activity.";
                        _publisherRejected = true;
                        _lastError = message ?? "Transmissor já ativo.";
                        PublisherRejected?.Invoke(_lastError);
                        _firstHandshake?.TrySetException(new InvalidOperationException(_lastError));
                        return;
                    }

                    case "viewer-count" when root.TryGetProperty("count", out var c):
                    {
                        var n = Math.Max(0, c.GetInt32());
                        Interlocked.Exchange(ref _viewers, n);
                        if (n == 0)
                        {
                            Interlocked.Exchange(ref _viewerLatency, 0);
                            LatencyChanged?.Invoke(LatencyMs);
                        }
                        ViewerCountChanged?.Invoke(n);
                        PublishDiagnostics();
                        break;
                    }

                    case "audience-capabilities":
                    {
                        var caps = new AudienceCapabilities(
                            root.TryGetProperty("viewers", out var v) ? Math.Max(0, v.GetInt32()) : ViewerCount,
                            root.TryGetProperty("readyViewers", out var rv) ? Math.Max(0, rv.GetInt32()) : 0,
                            root.TryGetProperty("ready", out var ready) && ready.GetBoolean(),
                            root.TryGetProperty("modeKey", out var mode) ? mode.GetString() ?? "720p30" : "720p30",
                            root.TryGetProperty("videoCodec", out var vc) ? vc.GetString() ?? "h264" : "h264",
                            root.TryGetProperty("videoProfile", out var vp) ? vp.GetString() ?? "baseline" : "baseline",
                            root.TryGetProperty("codecString", out var cs) ? cs.GetString() ?? "avc1.42E01F" : "avc1.42E01F",
                            root.TryGetProperty("compatibilityMode", out var cm) && cm.GetBoolean(),
                            root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? string.Empty : string.Empty);
                        AudienceCapabilitiesChanged?.Invoke(caps);
                        break;
                    }

                    case "request-keyframe":
                        KeyframeRequested?.Invoke();
                        break;

                    case "pong" when root.TryGetProperty("sentAt", out var s) && s.TryGetInt64(out var sent):
                    {
                        // Mantido por compatibilidade com relays antigos; este "pong" passa
                        // pela fila de processamento do Durable Object e não é mais a fonte
                        // usada para o latência exibida (veja o "pong" de texto puro acima).
                        _lastPongAt = Environment.TickCount64;
                        break;
                    }

                    case "latency-probe-ack" when root.TryGetProperty("sentAt", out var probe) && probe.TryGetInt64(out var probeSent):
                    {
                        // Full Capture -> relay -> viewer -> relay -> Capture latency.
                        // Adaptive quality should follow the experience of the viewer,
                        // while the plain-text pong remains the edge-relay diagnostic.
                        var rtt = Math.Max(0, Environment.TickCount64 - probeSent);
                        Interlocked.Exchange(ref _viewerLatency, rtt);
                        LatencyChanged?.Invoke(LatencyMs);
                        PublishDiagnostics();
                        break;
                    }

                    case "error":
                    {
                        var message = root.TryGetProperty("message", out var e) ? e.GetString() : "Erro no relay.";
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            _lastError = message!;
                            Error?.Invoke(message!);
                        }
                        break;
                    }
                }
            }
            catch
            {
                // Controle malformado é ignorado; mídia usa frames binários separados.
            }
        }
    }

    private async Task PingLoop(ClientWebSocket ws, CancellationToken token)
    {
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // O relay responde "pong" via WebSocket auto-response, sem acordar o Durable Object.
            // Também usamos o round-trip desse ping de texto como medida de latência (veja ReceiveLoop).
            Interlocked.Exchange(ref _lastPingSentAt, Environment.TickCount64);
            QueueControlText("ping");
            if (ViewerCount > 0)
                QueueControl(new { type = "latency-probe", sentAt = Environment.TickCount64 });

            await Task.Delay(6000, token);

            if (Environment.TickCount64 - Volatile.Read(ref _lastPongAt) > 18_000)
            {
                _lastError = "Heartbeat do relay expirou.";
                Error?.Invoke(_lastError);
                try { ws.Abort(); } catch { }
                return;
            }
        }
    }

    public RelayDiagnostics GetDiagnostics() => new(
        IsConnected,
        ViewerCount,
        LatencyMs,
        Interlocked.Read(ref _videoSent),
        Interlocked.Read(ref _audioSent),
        (_videoQueue?.Dropped ?? 0),
        Interlocked.Read(ref _audioDropped),
        (_videoQueue?.Count ?? 0),
        QueueCount(_audioQueue),
        _reconnects,
        _lastError);

    private void PublishDiagnostics() => DiagnosticsChanged?.Invoke(GetDiagnostics());

    private static int QueueCount(Channel<byte[]>? channel)
    {
        if (channel?.Reader.CanCount == true) return channel.Reader.Count;
        return 0;
    }

    public static bool IsValidCode(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Z2-9]{6}$");

    public static string Normalize(string value) => new(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(6).ToArray());

    public async ValueTask DisposeAsync() => await StopAsync();
}
