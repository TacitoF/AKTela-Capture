using System.Diagnostics;
using System.Reflection;
using System.Buffers.Binary;
using AKTelaCapture;

// Exercise overflow with real AKV5 packets: never emit a dependent delta after loss.
var queue = new VideoPacketQueue(2);
byte[] Packet(bool key) => PacketProtocol.Create(MediaKind.Video, key, 0, 33333, new byte[] { 1 });
var protocolPacket = Packet(true);
Check(protocolPacket[4] == 5, "Pacote incompatível com o protocolo AKV5 aceito pelo Relay/Activity");
Check(!queue.TryWrite(Packet(false)), "Delta aceito antes do primeiro IDR");
var idr = Packet(true);
Check(queue.TryWrite(idr) && queue.TryWrite(Packet(false)), "Fila não aceitou GOP inicial");
Check(!queue.TryWrite(Packet(false)) && queue.Count == 0, "Overflow não invalidou a cadeia de referência");
Check(!queue.TryWrite(Packet(false)), "Delta aceito depois de perda de referência");
Check(queue.TryWrite(idr) && queue.TryRead(out var resumed) && ReferenceEquals(idr, resumed), "IDR não recuperou a fila");
queue.Reset();
Check(!queue.TryWrite(Packet(false)), "Reconexão aceitou delta antigo");
Console.WriteLine("PASS fila de vídeo: início, overflow, recuperação e reconexão.");

var audioPacket = PacketProtocol.Create(MediaKind.Audio, true, 20_000, 20_000, new byte[] { 2 });
var mediaBatch = PacketProtocol.CreateBatch([idr, audioPacket, Packet(false)]);
Check(mediaBatch.AsSpan(0, 4).SequenceEqual("AKB1"u8), "Cabeçalho do lote de mídia inválido");
Check(BinaryPrimitives.ReadUInt16LittleEndian(mediaBatch.AsSpan(6, 2)) == 3, "Quantidade incorreta no lote de mídia");
var firstLength = BinaryPrimitives.ReadInt32LittleEndian(mediaBatch.AsSpan(PacketProtocol.BatchHeader, 4));
Check(firstLength == idr.Length, "Tamanho do primeiro pacote do lote inválido");
Console.WriteLine("PASS lote de mídia: vídeo e áudio agrupados em uma mensagem WebSocket.");

// A fonte sintética dispensa desktop/GPU, mas atravessa os mesmos argumentos,
// leitor de SPS, validação e loop de envio usados pela captura real.
var ffmpeg = args.Length > 0 ? args[0] : await FfmpegManager.EnsureAsync();
Console.WriteLine($"FFmpeg: {ffmpeg}");

foreach (var quality in QualityOption.All)
{
    foreach (var (profile, profileId) in new[] { ("baseline", 66), ("main", 77), ("high", 100) })
    {
        var cfg = Config(quality, profile);
        await using var streamer = new VideoStreamer();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        H264StreamInfo? info = null;
        var frames = 0;
        var keyframes = 0;
        var targetFrames = quality.Fps + 2;
        streamer.PacketReady += packet =>
        {
            if (PacketProtocol.IsKeyframe(packet))
            {
                keyframes++;
                info ??= H264AccessUnitReader.Inspect(packet.AsSpan(PacketProtocol.Header));
            }
            if (++frames >= targetFrames) cancellation.Cancel();
        };

        var result = await Run(streamer, Input(ffmpeg, cfg), cfg, cancellation.Token);
        Check(result.Ok && frames >= targetFrames,
            $"{quality.Key}/{profile}: captura encerrou com {frames} quadros: {result.Error}");
        Check(keyframes >= 2, $"{quality.Key}/{profile}: segundo quadro-chave não foi enviado");
        Check(info?.ProfileIdc == profileId,
            $"{quality.Key}/{profile}: SPS inesperado {info?.CodecString ?? "ausente"}");
        var expectedLevel = quality.Key switch
        {
            "720p30" => 31, "720p60" => 32, "1080p30" => 40, "1080p60" => 42,
            _ => throw new InvalidOperationException("Qualidade sem nível esperado no teste")
        };
        Check(info?.LevelIdc == expectedLevel,
            $"{quality.Key}/{profile}: nível inesperado {info?.LevelIdc}");
        Console.WriteLine($"PASS {quality.Key}/{profile}: {frames} quadros, {keyframes} keyframes, {info!.CodecString}");
    }
}

await Reject(
    Config(QualityOption.ByKey("720p30"), "baseline"),
    Config(QualityOption.ByKey("720p30"), "main"),
    "perfil solicitado main");
await Reject(
    Config(QualityOption.ByKey("1080p60"), "main"),
    Config(QualityOption.ByKey("720p30"), "main"),
    "nível H.264");
Console.WriteLine("PASS: 12 transmissões H.264 contínuas e 2 rejeições de configurações incompatíveis.");

async Task Reject(StreamConfig encoded, StreamConfig negotiated, string expectedError)
{
    await using var streamer = new VideoStreamer();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var sent = 0;
    streamer.PacketReady += _ => sent++;
    var result = await Run(streamer, Input(ffmpeg, encoded), negotiated, cancellation.Token);
    Check(!result.Ok && sent == 0 && result.Error.Contains(expectedError, StringComparison.Ordinal),
        $"Configuração incompatível não foi rejeitada corretamente: {sent} quadros, {result.Error}");
    Console.WriteLine($"PASS rejeição: {result.Error}");
}

static StreamConfig Config(QualityOption q, string profile) => new(
    q.Key, q.Width, q.Height, q.Fps, q.BitrateMbps,
    false, "Jogo", "Ocultar", "h264", profile, "", false);

static ProcessStartInfo Input(string ffmpeg, StreamConfig cfg)
{
    var psi = new ProcessStartInfo(ffmpeg)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var arg in new[]
    {
        "-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i",
        $"testsrc2=size={cfg.Width}x{cfg.Height}:rate={cfg.Fps}"
    }) psi.ArgumentList.Add(arg);
    // Invoca o construtor real de argumentos; não mantém uma segunda cópia.
    Method("X264", BindingFlags.Static).Invoke(null, new object[] { psi, cfg });
    return psi;
}

static async Task<(bool Ok, string Error)> Run(
    VideoStreamer streamer, ProcessStartInfo psi, StreamConfig cfg, CancellationToken token)
{
    var task = (Task<(bool Ok, string Error)>)Method("RunH264Attempt", BindingFlags.Instance)
        .Invoke(streamer, new object[] { "Smoke test H.264", psi, cfg, token })!;
    return await task.WaitAsync(TimeSpan.FromSeconds(50));
}

static MethodInfo Method(string name, BindingFlags scope) =>
    typeof(VideoStreamer).GetMethod(name, BindingFlags.NonPublic | scope)
    ?? throw new MissingMethodException(typeof(VideoStreamer).FullName, name);

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
