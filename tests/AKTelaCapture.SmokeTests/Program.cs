using System.Diagnostics;
using System.Reflection;
using System.Buffers.Binary;
using System.Drawing;
using AKTelaCapture;

Check(RelayClient.MediaBatchWindowMs == 20,
    "Janela de lote não acompanha um bloco Opus de 20 ms");
Check(RelayClient.AudioCapacity * 20 >= 140,
    "Fila de áudio não absorve uma oscilação curta de envio");
Console.WriteLine("PASS transporte de áudio: lotes de 20 ms e reserva curta contra jitter.");

Check(QualityOption.LowerForPerformance("1080p60") == "720p60" &&
      QualityOption.LowerForPerformance("720p60") == "720p30",
    "Adaptação local não preserva 60 FPS antes de reduzir a fluidez");
Check(VideoStreamer.IsSoftwareEncoder("Software H.264") &&
      VideoStreamer.IsSoftwareEncoder("Software VP8 · compatibilidade") &&
      !VideoStreamer.IsSoftwareEncoder("NVENC · GPU direto"),
    "Detecção de encoder por software está incorreta");
Console.WriteLine("PASS proteção do jogo: redução 1080p60 → 720p60 → 720p30 e detecção de software.");

var performance = new PerformanceQualityPolicy();
performance.Reset("1080p60");
Check(!performance.Evaluate("1080p60", "1080p60", 60, 40, true, false, true, 0),
    "Um pico isolado reduziu a qualidade");
Check(performance.Evaluate("1080p60", "1080p60", 60, 40, true, false, true, 5000) && performance.LimitKey == "720p60",
    "Sobrecarga consecutiva não protegeu o jogo");
for (long now = 10_000; now < 70_000; now += 5000)
    Check(!performance.Evaluate("1080p60", "720p60", 60, 60, true, false, true, now),
        "Qualidade restaurada sem estabilidade suficiente");
Check(performance.Evaluate("1080p60", "720p60", 60, 60, true, false, true, 70_000) && performance.LimitKey == "1080p60",
    "Qualidade não voltou após um minuto estável");
performance.LimitForSoftware();
for (long now = 80_000; now <= 180_000; now += 5000)
    performance.Evaluate("1080p60", "720p30", 30, 30, true, true, true, now);
Check(performance.LimitKey == "720p30", "Encoder por software perdeu sua proteção de CPU");
performance.Reset("720p60");
performance.Evaluate("720p60", "720p60", 60, 40, true, false, true, 0);
performance.Evaluate("720p60", "720p60", 60, 40, true, false, true, 5000);
performance.Evaluate("720p60", "720p30", 30, 30, true, false, true, 10_000);
performance.Evaluate("720p60", "720p30", 30, 0, false, false, true, 65_000);
Check(!performance.Evaluate("720p60", "720p30", 30, 30, true, false, true, 80_000),
    "Tempo pausado foi contado como estabilidade");
Check(QualityOption.HigherForPerformance("720p30", "1080p60") == "720p60" &&
      QualityOption.HigherForPerformance("720p60", "1080p60") == "1080p60",
    "Recuperação de qualidade sacrificou os 60 FPS");
Console.WriteLine("PASS adaptação local: recuperação gradual, pausa e proteção de software.");

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

// Uma rajada processada no mesmo instante ainda precisa representar três blocos
// consecutivos de 20 ms; timestamps baseados no relógio do encoder causavam sobreposição.
var audioClock = new FixedFrameTimestampClock();
var audioTs1 = audioClock.Next(1_000_000, 20_000);
var audioTs2 = audioClock.Next(1_000_100, 20_000);
var audioTs3 = audioClock.Next(1_000_200, 20_000);
Check(audioTs1 == 1_000_000 && audioTs2 == 1_020_000 && audioTs3 == 1_040_000,
    "Relógio de áudio comprimiu blocos processados em rajada");
audioClock.Reset();
Check(audioClock.Next(2_000_000, 20_000) == 2_000_000, "Relógio de áudio não reiniciou");
Check(audioClock.Next(3_000_000, 20_000) == 3_000_000,
    "Relógio de mídia manteve uma timeline antiga depois de uma interrupção longa");
Console.WriteLine("PASS relógio de mídia: duração contínua, rajadas e reancoragem.");

// A fonte sintética dispensa desktop/GPU, mas atravessa os mesmos argumentos,
// leitor de SPS, validação e loop de envio usados pela captura real.
var ffmpeg = args.Length > 0 ? args[0] : await FfmpegManager.EnsureAsync();
Console.WriteLine($"FFmpeg: {ffmpeg}");
Check(await FfmpegManager.SupportsGfxCaptureAsync(ffmpeg),
    "O FFmpeg publicado não contém gfxcapture e scale_d3d11 necessários para o caminho GPU");

var windowSource = new CaptureSource(
    SourceKind.Window, "Teste · janela", new Rectangle(40, 60, 1000, 700), 0,
    new IntPtr(0x1234), 1234, "teste");
var windowConfig = Config(QualityOption.ByKey("720p30"), "main");
var gfxProcess = (ProcessStartInfo)Method("BuildGfxX264", BindingFlags.Static)
    .Invoke(null, new object[] { ffmpeg, windowSource, windowConfig })!;
var gfxArguments = string.Join("\n", gfxProcess.ArgumentList);
Check(gfxArguments.Contains("gfxcapture=hwnd=4660", StringComparison.Ordinal),
    "Captura moderna não recebeu o HWND exato da janela");
Check(gfxArguments.Contains("resize_mode=scale_aspect", StringComparison.Ordinal) &&
      gfxArguments.Contains("width=1280:height=720", StringComparison.Ordinal),
    "Captura de janela não preserva e centraliza a proporção no canvas negociado");
Check(gfxArguments.Contains("capture_border=1", StringComparison.Ordinal),
    "Captura moderna e limites visíveis da janela estão inconsistentes");

var gameConfig = Config(QualityOption.ByKey("720p60"), "main");
var gfxGpuProcess = (ProcessStartInfo)Method("BuildGfxNvencGpu", BindingFlags.Static)
    .Invoke(null, new object[] { ffmpeg, windowSource, gameConfig })!;
var gfxGpuArguments = string.Join("\n", gfxGpuProcess.ArgumentList);
Check(gfxGpuArguments.Contains("scale_d3d11=", StringComparison.Ordinal) &&
      !gfxGpuArguments.Contains("hwdownload", StringComparison.Ordinal),
    "Caminho rápido de janela ainda transfere cada frame para a RAM");
Check(gfxGpuArguments.Contains("max_framerate=60", StringComparison.Ordinal) &&
      gfxGpuArguments.Contains("fps=60", StringComparison.Ordinal) &&
      gfxGpuArguments.Contains("-preset\np2", StringComparison.Ordinal),
    "Perfil Jogo não estabiliza a cadência ou não usa o preset NVENC leve em 60 FPS");

var displaySource = new CaptureSource(
    SourceKind.Display, "Teste · tela", new Rectangle(0, 0, 1920, 1080), 0,
    IntPtr.Zero, 0, "display");
var ddaGpuProcess = (ProcessStartInfo)Method("BuildDdaNvenc", BindingFlags.Static)
    .Invoke(null, new object[] { ffmpeg, displaySource, windowConfig, true })!;
var ddaGpuArguments = string.Join("\n", ddaGpuProcess.ArgumentList);
Check(ddaGpuArguments.Contains("scale_d3d11=", StringComparison.Ordinal) &&
      !ddaGpuArguments.Contains("hwdownload", StringComparison.Ordinal),
    "Caminho rápido da tela ainda transfere cada frame para a RAM");
foreach (var (builder, source) in new[] { ("BuildGfxMfGpu", windowSource), ("BuildDdaMfGpu", displaySource) })
{
    var process = (ProcessStartInfo)Method(builder, BindingFlags.Static)
        .Invoke(null, new object[] { ffmpeg, source, windowConfig })!;
    var arguments = string.Join("\n", process.ArgumentList);
    Check(arguments.Contains("scale_d3d11=", StringComparison.Ordinal) &&
          arguments.Contains("h264_mf", StringComparison.Ordinal) &&
          arguments.Contains("-hw_encoding\n1", StringComparison.Ordinal) &&
          !arguments.Contains("hwdownload", StringComparison.Ordinal),
        "Media Foundation não manteve captura/escala em superfícies da GPU");
}
var ultrawideSource = displaySource with { Bounds = new Rectangle(0, 0, 3440, 1440) };
Check(VideoStreamer.HasCompatibleAspectRatio(displaySource, windowConfig) &&
      !VideoStreamer.HasCompatibleAspectRatio(ultrawideSource, windowConfig),
    "Caminho GPU pode deformar monitores cuja proporção não seja 16:9");

var gdiProcess = (ProcessStartInfo)Method("BuildGdiX264", BindingFlags.Static)
    .Invoke(null, new object[] { ffmpeg, windowSource, windowConfig })!;
var gdiArguments = string.Join("\n", gdiProcess.ArgumentList);
Check(gdiArguments.Contains("force_original_aspect_ratio=decrease", StringComparison.Ordinal) &&
      gdiArguments.Contains("pad=1280:720", StringComparison.Ordinal),
    "Fallback GDI pode deformar ou desalinhar a janela");
Check(gdiArguments.Contains("-threads:v", StringComparison.Ordinal) &&
      gdiArguments.Contains("-filter_threads", StringComparison.Ordinal),
    "Fallback por software voltou a ocupar todos os núcleos da máquina");
Console.WriteLine("PASS captura: GPU direto sem cópia para RAM e fallbacks centralizados.");

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
