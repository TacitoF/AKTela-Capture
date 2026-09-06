using System.Runtime.InteropServices;

namespace AKTelaCapture;

internal sealed partial class MainForm : Form
{
    private readonly RelayClient _relay = new();
    private readonly VideoStreamer _video = new();
    private readonly AudioStreamer _audio = new();
    private readonly CursorTracker _cursor;

    private readonly ComboBox _quality = new();
    private readonly ComboBox _sourceType = new();
    private readonly ComboBox _source = new();
    private readonly TextBox _code = new();
    private readonly Label _codeValidation = new();
    private readonly CheckBox _audioCheck = new();
    private readonly Button _start = new();
    private readonly Button _paste = new();
    private readonly Label _status = new();
    private readonly Label _detail = new();
    private readonly Label _outputValue = new();
    private readonly Label _fpsValue = new();
    private readonly Label _encoderValue = new();
    private readonly Label _viewerValue = new();
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayStatusItem = new();
    private readonly System.Windows.Forms.Timer _adaptiveTimer = new() { Interval = 5000 };

    private readonly SemaphoreSlim _mediaGate = new(1, 1);

    private const int HotkeyId = 0xA711;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkS = 0x53;

    private bool _hotkeyRegistered;
    private bool _toggleBusy;
    private bool _sharing;
    private bool _relayConnected;
    private bool _allowClose;
    private bool _publisherBlocked;

    private CaptureSource? _activeSource;
    private StreamConfig? _activeConfig;
    private AudioMode _activeAudio;
    private string _preset = "Leve";
    private AudienceCapabilities _audience = AudienceCapabilities.Default();
    private string _networkCapKey = "1080p60";
    private string _roomCapKey = "1080p60";
    private int _streamSlot;
    private int _activeStreams = 1;
    private long _lastDropSnapshot;
    private int _stableTicks;
    private long _lastKeyframeRestartAt;

    private static readonly Color Bg = Color.FromArgb(16, 19, 25);
    private static readonly Color Surface = Color.FromArgb(24, 29, 37);
    private static readonly Color Surface2 = Color.FromArgb(33, 40, 51);
    private static readonly Color TextColor = Color.FromArgb(239, 243, 250);
    private static readonly Color Muted = Color.FromArgb(155, 168, 187);
    private static readonly Color Accent = Color.FromArgb(111, 231, 193);
    private static readonly Color Red = Color.FromArgb(245, 85, 105);
    private static readonly Color Green = Color.FromArgb(61, 211, 157);
    private static readonly Color Yellow = Color.FromArgb(224, 178, 76);

    public MainForm()
    {
        _cursor = new CursorTracker(_relay);

        Icon = AppIcon.Load();
        base.Text = "AKTela Capture";
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        // Painel de controle compacto: o formato retrato é parte da identidade.
        // A janela é limitada à área útil do monitor e pode ser redimensionada;
        // o conteúdo central permanece rolável em telas menores ou DPI alto.
        ClientSize = new Size(560, 860);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = new Size(440, 540);
        SizeGripStyle = SizeGripStyle.Show;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        BuildTray();
        Wire();
        LoadSources();

        _adaptiveTimer.Tick += async (_, _) => await EvaluateAdaptiveQuality();
        FormClosing += OnClosing;
        Shown += (_, _) => FitWindowToWorkingArea();
        DpiChanged += (_, _) => BeginInvoke((Action)FitWindowToWorkingArea);
    }

    private void FitWindowToWorkingArea()
    {
        if (IsDisposed || Disposing) return;
        var workingArea = Screen.FromControl(this).WorkingArea;
        var dpiScale = Math.Max(1f, DeviceDpi / 96f);
        var nonClientWidth = Math.Max(0, Width - ClientSize.Width);
        var nonClientHeight = Math.Max(0, Height - ClientSize.Height);
        var preferredSize = new Size(
            Math.Max(Width, (int)Math.Ceiling(560 * dpiScale) + nonClientWidth),
            Math.Max(Height, (int)Math.Ceiling(860 * dpiScale) + nonClientHeight));
        Bounds = FitBoundsToWorkingArea(workingArea, preferredSize);
    }

    internal static Rectangle FitBoundsToWorkingArea(Rectangle workingArea, Size requestedSize)
    {
        const int edgeGap = 12;
        var availableWidth = Math.Max(1, workingArea.Width - edgeGap * 2);
        var availableHeight = Math.Max(1, workingArea.Height - edgeGap * 2);
        var width = Math.Min(Math.Max(1, requestedSize.Width), availableWidth);
        var height = Math.Min(Math.Max(1, requestedSize.Height), availableHeight);
        var x = workingArea.Left + Math.Max(edgeGap, (workingArea.Width - width) / 2);
        var y = workingArea.Top + Math.Max(edgeGap, (workingArea.Height - height) / 2);
        return new Rectangle(x, y, width, height);
    }

    private void ApplyPreset(string preset)
    {
        _preset = preset;
        if (preset == "Jogo")
        {
            _sourceType.SelectedItem = "Janela";
            _quality.SelectedItem = QualityOption.All.First(q => q.Key == "1080p60");
        }
        else if (preset == "Filme")
        {
            _sourceType.SelectedItem = "Janela";
            _quality.SelectedItem = QualityOption.All.First(q => q.Key == "1080p30");
        }
        else
        {
            _sourceType.SelectedItem = "Tela";
            _quality.SelectedItem = QualityOption.All.First(q => q.Key == "720p30");
        }
        UpdatePresetButtons();
        LoadSources();
    }

    private void Wire()
    {
        _sourceType.SelectedIndexChanged += (_, _) => LoadSources();
        _start.Click += async (_, _) => await Toggle();

        _relay.ConnectionChanged += connected => Ui(() =>
        {
            _relayConnected = connected;
            if (!connected && _sharing) _ = SyncMedia(0);
            RefreshStatus();
        });

        _relay.ViewerCountChanged += count => Ui(() =>
        {
            _viewerValue.Text = Math.Max(0, count).ToString();
            RefreshStatus();
            if (count <= 0) _ = SyncMedia(0);
            else if (_audience.Ready) _ = SyncMedia(count);
        });

        _relay.LatencyChanged += _ => Ui(RefreshStatus);

        _relay.AudienceCapabilitiesChanged += caps => Ui(() =>
        {
            _audience = caps;
            RefreshStatus();
            if (_sharing && caps.Viewers > 0 && caps.Ready) _ = NegotiateAndSync(caps.Viewers);
        });

        _relay.RoomPolicyChanged += (activeStreams, _, maxModeKey) => Ui(() =>
        {
            _activeStreams = activeStreams;
            _roomCapKey = maxModeKey;
            RefreshStatus();
            if (_sharing) _ = ApplyEffectiveConfig("limite automático para múltiplas telas");
        });

        _relay.KeyframeRequested += () => Ui(() => _ = RestartForKeyframe());
        // Antes, uma rajada de descartes só era percebida no próximo tick do timer de
        // 5s, deixando a imagem travada em quem assistia até lá. Reagir no próprio
        // evento de congestionamento reduz a qualidade quase imediatamente.
        _relay.VideoCongested += () => Ui(() => _ = OnVideoCongested());

        _relay.PublisherRejected += message => Ui(() =>
        {
            _publisherBlocked = true;
            SetStatus("Limite de telas atingido", Red, message);
        });

        _relay.Error += msg => Ui(() =>
        {
            if (_publisherBlocked) return;
            _detail.Text = msg;
            _detail.ForeColor = Yellow;
        });

        _video.PacketReady += packet => _relay.QueuePacket(packet);
        _audio.PacketReady += packet => _relay.QueuePacket(packet);

        _video.FpsChanged += fps => Ui(() =>
        {
            _fpsValue.Text = fps > 0 ? $"{fps:0} FPS" : "—";
            if (_sharing && fps > 0) RefreshStatus();
        });

        _video.EncoderChanged += name => Ui(() =>
        {
            _encoderValue.Text = ShortEncoder(name);
            if (_sharing) RefreshStatus();
        });

        _video.CodecChanged += (codec, profile, codecString) =>
        {
            var current = _activeConfig;
            if (current is null) return;

            // Atualiza o relay imediatamente, antes de o primeiro quadro-chave sair do encoder.
            // Assim o espectador nunca recebe um IDR seguido de uma configuração que invalida o decoder.
            if (current.VideoCodec != codec || current.VideoProfile != profile || current.ExpectedCodec != codecString)
            {
                var updated = current with
                {
                    VideoCodec = codec,
                    VideoProfile = profile,
                    ExpectedCodec = codecString
                };
                _activeConfig = updated;
                _relay.UpdateStreamConfig(updated);
            }
            Ui(RefreshStatus);
        };

        _video.StreamError += msg => Ui(() =>
        {
            SetStatus("Falha no encoder", Red, "Abra o diagnóstico para detalhes.");
            MessageBox.Show(this, msg, "Falha na captura", MessageBoxButtons.OK, MessageBoxIcon.Error);
        });

        _audio.Error += msg => Ui(() =>
            MessageBox.Show(this, msg, "Falha no áudio", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    private async Task NegotiateAndSync(int viewers)
    {
        await ApplyEffectiveConfig("recursos dos espectadores");
        await SyncMedia(viewers);
    }

    private void LoadSources()
    {
        if (_sharing) return;
        var selected = _source.SelectedItem as CaptureSource;
        _source.Items.Clear();
        var type = _sourceType.SelectedItem?.ToString() ?? "Tela";
        var sources = type == "Janela" ? SourceEnumerator.Windows() : SourceEnumerator.Displays();
        foreach (var source in sources) _source.Items.Add(source);
        if (_source.Items.Count > 0)
        {
            _source.SelectedItem = _source.Items.Cast<CaptureSource>().FirstOrDefault(s =>
                s.Kind == selected?.Kind && s.WindowHandle == selected.WindowHandle && s.OutputIndex == selected.OutputIndex);
            if (_source.SelectedIndex < 0) _source.SelectedIndex = 0;
        }
        UpdateSourceSummary();
        _start.Enabled = _source.Items.Count > 0;
    }

    private static (int Width, int Height) FitOutput(CaptureSource source, QualityOption quality)
    {
        var sourceW = Math.Max(2, source.Width);
        var sourceH = Math.Max(2, source.Height);
        var scale = Math.Min(quality.Width / (double)sourceW, quality.Height / (double)sourceH);
        var width = Math.Max(2, ((int)Math.Round(sourceW * scale)) & ~1);
        var height = Math.Max(2, ((int)Math.Round(sourceH * scale)) & ~1);
        return (width, height);
    }

    private static string CodecStringFor(string qualityKey, string codec, string profile)
    {
        if (codec == "vp8") return "vp8";
        var prefix = profile switch { "main" => "4D40", "high" => "6400", _ => "42E0" };
        var level = qualityKey switch { "1080p60" => "2A", "1080p30" => "28", "720p60" => "20", _ => "1F" };
        return $"avc1.{prefix}{level}";
    }

    private StreamConfig BuildEffectiveConfig(CaptureSource source, QualityOption requested)
    {
        var requestedKey = requested.Key;
        var audienceKey = _audience.Viewers > 0 ? _audience.ModeKey : requestedKey;
        var effectiveKey = QualityOption.Min(QualityOption.Min(QualityOption.Min(requestedKey, audienceKey), _networkCapKey), _roomCapKey);

        var codec = _audience.Viewers > 0 ? _audience.VideoCodec : "h264";
        var profile = _audience.Viewers > 0 ? _audience.VideoProfile : "main";
        if (codec == "vp8") effectiveKey = "720p30";
        if (profile != "baseline" && profile != "main" && profile != "high") profile = "baseline";

        var quality = QualityOption.ByKey(effectiveKey);
        var output = FitOutput(source, quality);
        var audioMode = _audioCheck.Checked
            ? (source.Kind == SourceKind.Window ? AudioMode.SourceOnly : AudioMode.SystemWithoutDiscord)
            : AudioMode.Off;

        var compatibility = _audience.CompatibilityMode || effectiveKey != requestedKey || codec == "vp8" || !_audience.Ready;
        return new StreamConfig(
            effectiveKey,
            output.Width,
            output.Height,
            quality.Fps,
            quality.BitrateMbps,
            audioMode != AudioMode.Off,
            _preset,
            _preset == "Jogo" ? "Ocultar" : "Mostrar",
            codec,
            profile,
            CodecStringFor(effectiveKey, codec, profile),
            compatibility);
    }

    private async Task Toggle()
    {
        if (_toggleBusy) return;
        _toggleBusy = true;
        try
        {
            if (_sharing)
            {
                await Stop();
                return;
            }

            if (_source.SelectedItem is not CaptureSource source || _quality.SelectedItem is not QualityOption requested) return;
            var code = RelayClient.Normalize(_code.Text);
            if (!RelayClient.IsValidCode(code))
            {
                SetStatus("Confira o código", Yellow, "Digite os 6 caracteres da Activity.");
                _code.Focus();
                return;
            }
            _publisherBlocked = false;
            _audience = AudienceCapabilities.Default();
            _networkCapKey = requested.Key;
            _roomCapKey = "1080p60";
            _streamSlot = 0;
            _activeStreams = 1;
            _stableTicks = 0;
            _lastDropSnapshot = 0;

            var initial = BuildEffectiveConfig(source, requested);
            _activeAudio = _audioCheck.Checked
                ? (source.Kind == SourceKind.Window ? AudioMode.SourceOnly : AudioMode.SystemWithoutDiscord)
                : AudioMode.Off;

            try
            {
                Lock(true);
                _start.Enabled = false;
                SetStatus("Verificando relay", Yellow, "Teste 1/4");
                await RelayClient.CheckHealthAsync();

                SetStatus("Preparando encoder", Yellow, "Teste 2/4 · FFmpeg");
                if (!File.Exists(FfmpegManager.PathToExe))
                {
                    var progress = new Progress<int>(p => Ui(() => SetStatus($"Preparando encoder · {p}%", Yellow, "Primeira execução")));
                    await FfmpegManager.EnsureAsync(progress);
                }

                SetStatus("Validando codec", Yellow, "Teste 3/4 · caminho de compatibilidade");
                var probeConfig = initial with
                {
                    QualityKey = "720p30",
                    Width = 1280,
                    Height = 720,
                    Fps = 30,
                    BitrateMbps = 4,
                    VideoCodec = "h264",
                    VideoProfile = "main",
                    ExpectedCodec = "avc1.4D401F",
                    CompatibilityMode = true
                };
                string probeEncoder;
                try
                {
                    probeEncoder = await _video.ProbeAsync(probeConfig);
                }
                catch
                {
                    probeConfig = probeConfig with { VideoCodec = "vp8", VideoProfile = "compatibility", ExpectedCodec = "vp8" };
                    probeEncoder = await _video.ProbeAsync(probeConfig);
                }
                _encoderValue.Text = ShortEncoder(probeEncoder);

                SetStatus("Verificando áudio", Yellow, "Teste 4/4");
                if (_activeAudio == AudioMode.SystemWithoutDiscord && ProcessTreeHelper.FindDiscordRootProcessId() is null)
                    _detail.Text = "Discord não detectado para exclusão de áudio; o vídeo continuará normalmente.";

                SetStatus("Conectando ao relay", Yellow, "Reservando esta Activity para um transmissor");
                await _relay.StartAsync(code, initial);

                _streamSlot = _relay.StreamSlot;
                _activeStreams = _relay.ActiveStreams;
                _roomCapKey = _relay.RoomMaxModeKey;
                initial = BuildEffectiveConfig(source, requested);
                _relay.UpdateStreamConfig(initial);

                _sharing = true;
                _activeSource = source;
                _activeConfig = initial;
                _outputValue.Text = $"{initial.Width}×{initial.Height}";
                _viewerValue.Text = Math.Max(0, _relay.ViewerCount).ToString();
                Lock(true);
                _start.Text = "Encerrar transmissão";
                StyleButton(_start, Red);
                _adaptiveTimer.Start();
                RefreshStatus();

                if (_relay.ViewerCount > 0 && _audience.Ready)
                    await NegotiateAndSync(_relay.ViewerCount);
            }
            catch (Exception ex)
            {
                await Stop();
                SetStatus("Não foi possível iniciar", Red, ex.Message);
                MessageBox.Show(this, ex.Message, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _start.Enabled = _sharing || _source.Items.Count > 0;
            }
        }
        finally
        {
            _toggleBusy = false;
        }
    }

    private async Task ApplyEffectiveConfig(string reason)
    {
        if (!_sharing || _activeSource is null || _quality.SelectedItem is not QualityOption requested) return;
        await _mediaGate.WaitAsync();
        try
        {
            if (!_sharing || _activeSource is null) return;
            var next = BuildEffectiveConfig(_activeSource, requested);
            var current = _activeConfig;
            if (current is not null && SameVideoConfig(current, next))
            {
                _activeConfig = next;
                RefreshStatus();
                return;
            }

            _activeConfig = next;
            _outputValue.Text = $"{next.Width}×{next.Height}";
            _relay.UpdateStreamConfig(next);

            if (_relay.ViewerCount > 0 && _relayConnected && _audience.Ready)
            {
                SetStatus(next.CompatibilityMode ? "Modo compatibilidade" : "Ajustando transmissão", Yellow, reason);
                _cursor.Stop();
                if (_video.IsRunning)
                {
                    await _video.RestartAsync(_activeSource, next);
                    _cursor.Start(_activeSource, () => next.CursorPolicy == "Mostrar");
                }
            }
            RefreshStatus();
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    private static bool SameVideoConfig(StreamConfig a, StreamConfig b) =>
        a.Width == b.Width && a.Height == b.Height && a.Fps == b.Fps && a.VideoCodec == b.VideoCodec && a.VideoProfile == b.VideoProfile && a.BitrateMbps == b.BitrateMbps;

    private async Task SyncMedia(int viewers)
    {
        await _mediaGate.WaitAsync();
        try
        {
            if (!_sharing) return;
            if (viewers <= 0 || !_relayConnected)
            {
                _cursor.Stop();
                await _video.StopAsync();
                await _audio.StopAsync();
                return;
            }

            // Não inicia vídeo antes de todos os espectadores anunciarem os codecs suportados.
            if (!_audience.Ready) return;
            if (_video.IsRunning) return;
            var source = _activeSource;
            var config = _activeConfig;
            if (source is null || config is null) return;

            SetStatus(config.CompatibilityMode ? "Modo compatibilidade" : "Preparando vídeo", Yellow, "Sincronizando primeiro quadro-chave");
            await _video.StartAsync(source, config);
            if (_activeAudio != AudioMode.Off) await _audio.StartAsync(_activeAudio, source.ProcessId);
            _cursor.Start(source, () => config.CursorPolicy == "Mostrar");
        }
        catch (Exception ex)
        {
            Ui(() => MessageBox.Show(this, ex.Message, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    private async Task RestartForKeyframe()
    {
        if (!_sharing || !_relayConnected || _relay.ViewerCount <= 0 || !_video.IsRunning || _activeSource is null || _activeConfig is null) return;
        var now = Environment.TickCount64;
        if (now - _lastKeyframeRestartAt < 1400) return;
        _lastKeyframeRestartAt = now;

        await _mediaGate.WaitAsync();
        try
        {
            if (!_sharing || !_relayConnected || _relay.ViewerCount <= 0 || _activeSource is null || _activeConfig is null) return;
            SetStatus("Sincronizando vídeo", Yellow, "Novo quadro-chave solicitado pelo espectador");
            await _video.RestartAsync(_activeSource, _activeConfig);
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    private long _lastCongestionReactAt;

    private async Task OnVideoCongested()
    {
        // Uma única rajada de rede derruba vários pacotes de uma vez (fila inteira +
        // o novo quadro); sem esse limite, cada descarte da mesma rajada dispararia
        // uma reavaliação/reinício de qualidade redundante.
        var now = Environment.TickCount64;
        if (now - _lastCongestionReactAt < 1000) return;
        _lastCongestionReactAt = now;
        await EvaluateAdaptiveQuality();
    }

    private async Task EvaluateAdaptiveQuality()
    {
        if (!_sharing || _relay.ViewerCount <= 0 || _quality.SelectedItem is not QualityOption requested) return;
        var diagnostics = _relay.GetDiagnostics();
        var drops = diagnostics.VideoDropped;
        var deltaDrops = Math.Max(0, drops - _lastDropSnapshot);
        _lastDropSnapshot = drops;

        // Limiar reduzido de 4 para 2: com a reação imediata ao evento de
        // congestionamento, uma única rajada (fila cheia = capacidade + 1 descartes)
        // já deve ser suficiente para acionar a queda de qualidade.
        var poor = diagnostics.LatencyMs > 320 || deltaDrops >= 2;
        var stable = diagnostics.LatencyMs > 0 && diagnostics.LatencyMs < 180 && deltaDrops == 0;

        if (poor)
        {
            _stableTicks = 0;
            var lowered = QualityOption.LowerOneStep(_networkCapKey);
            if (lowered != _networkCapKey)
            {
                _networkCapKey = lowered;
                await ApplyEffectiveConfig("rede congestionada; qualidade reduzida automaticamente");
            }
            return;
        }

        if (stable)
        {
            _stableTicks++;
            if (_stableTicks >= 9)
            {
                _stableTicks = 0;
                var raised = QualityOption.HigherOneStep(_networkCapKey, requested.Key);
                if (raised != _networkCapKey)
                {
                    _networkCapKey = raised;
                    await ApplyEffectiveConfig("rede estável; qualidade elevada gradualmente");
                }
            }
        }
        else
        {
            _stableTicks = 0;
        }
    }

    private async Task Stop()
    {
        _adaptiveTimer.Stop();
        _sharing = false;
        _publisherBlocked = false;
        await _mediaGate.WaitAsync();
        try
        {
            _cursor.Stop();
            await _video.StopAsync();
            await _audio.StopAsync();
            await _relay.StopAsync();
        }
        finally { _mediaGate.Release(); }
        _activeSource = null;
        _activeConfig = null;
        _audience = AudienceCapabilities.Default();
        _roomCapKey = "1080p60";
        _streamSlot = 0;
        _activeStreams = 1;
        _outputValue.Text = "—";
        _fpsValue.Text = "—";
        _encoderValue.Text = "—";
        _viewerValue.Text = "0";
        Lock(false);
        _start.Text = "Iniciar transmissão";
        StyleButton(_start, Accent);
        SetStatus("Pronto", Muted, "Cole o código exibido na Activity");
    }

    private void RefreshStatus()
    {
        if (!_sharing)
        {
            if (!_publisherBlocked) SetStatus("Pronto", Muted, "Cole o código exibido na Activity");
            return;
        }
        if (!_relayConnected)
        {
            SetStatus("Reconectando ao relay", Yellow, "Reconexão automática com espera progressiva");
            return;
        }
        if (_relay.ViewerCount == 0)
        {
            SetStatus("Ligado · aguardando espectador", Green, CurrentDetail());
            return;
        }
        if (!_audience.Ready)
        {
            if (_audience.Viewers > 0 && _audience.ReadyViewers >= _audience.Viewers && _audience.Reason.Contains("nenhum codec", StringComparison.OrdinalIgnoreCase))
                SetStatus("Sem codec compatível", Red, "O espectador não oferece H.264/VP8 compatível com esta Activity.");
            else
                SetStatus("Negociando compatibilidade", Yellow, $"{_audience.ReadyViewers}/{_audience.Viewers} espectadores verificados");
            return;
        }
        if (_activeConfig?.CompatibilityMode == true)
        {
            SetStatus($"Modo compatibilidade · {_relay.ViewerCount} assistindo", Yellow, CurrentDetail());
            return;
        }
        SetStatus($"Ao vivo · {_relay.ViewerCount} assistindo", _relay.LatencyMs > 450 ? Yellow : Green, CurrentDetail());
    }

    private string CurrentDetail()
    {
        var details = new List<string>();
        if (_streamSlot > 0) details.Add($"Tela {_streamSlot}/3");
        if (_activeStreams > 1) details.Add($"{_activeStreams} telas · modo leve");
        if (_activeConfig is not null) details.Add(_activeConfig.ModeLabel);
        if (_activeConfig is not null) details.Add(_activeConfig.VideoCodec == "vp8" ? "VP8" : $"H.264 {_activeConfig.VideoProfile}");
        if (_encoderValue.Text is not "—" and not "") details.Add(_encoderValue.Text);
        if (_relay.LatencyMs > 0) details.Add($"{_relay.LatencyMs} ms");
        return details.Count > 0 ? string.Join(" · ", details) : "Transmissão ativa";
    }

    private void SetStatus(string text, Color color, string detail)
    {
        var live = text.StartsWith("Ao vivo", StringComparison.OrdinalIgnoreCase);
        _status.Text = live ? $"AO VIVO{text[7..]}" : text;
        _status.ForeColor = live ? Color.White : color;
        _status.BackColor = live ? Color.FromArgb(126, 36, 51) : Color.Transparent;
        _status.Padding = live ? new Padding(8, 4, 8, 4) : Padding.Empty;
        _detail.Text = detail;
        _detail.ForeColor = color == Red ? Color.FromArgb(255, 220, 225) : Muted;
        _detail.BackColor = color == Red ? Color.FromArgb(77, 35, 43) : Color.Transparent;
        _detail.Padding = color == Red ? new Padding(8, 6, 8, 4) : Padding.Empty;
        _statusDot.ForeColor = live ? Red : color;
        UpdateTray();
    }

    private void Lock(bool locked)
    {
        _code.ReadOnly = locked;
        _code.TabStop = !locked;
        _paste.Enabled = !locked;
        _refreshSources.Enabled = !locked;
        foreach (var button in _presetButtons.Values) button.Enabled = !locked;
        _quality.Enabled = !locked;
        _sourceType.Enabled = !locked;
        _source.Enabled = !locked;
        _audioCheck.Enabled = !locked;
        _audioCheck.AutoCheck = !locked;
        _audioCheck.ForeColor = TextColor;
    }

    private void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "AKTela Capture";
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };
        _tray.DoubleClick += (_, _) => Restore();

        var menu = new ContextMenuStrip();
        _trayStatusItem.Enabled = false;
        _trayStatusItem.Text = "Pronto";
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir AKTela Capture", null, (_, _) => Restore());
        menu.Items.Add("Iniciar/encerrar   Ctrl+Shift+S", null, async (_, _) => await Toggle());
        menu.Items.Add("Diagnóstico", null, (_, _) => ShowDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, async (_, _) =>
        {
            if (_toggleBusy) return;
            _allowClose = true;
            _toggleBusy = true;
            await Stop();
            Close();
        });
        _tray.ContextMenuStrip = menu;
    }

    private void ShowDiagnostics()
    {
        var relay = _relay.GetDiagnostics();
        var video = _video.GetDiagnostics();
        var config = _activeConfig;
        var text = string.Join(Environment.NewLine, new[]
        {
            $"Estado: {_status.Text}",
            $"Relay: {(relay.Connected ? "conectado" : "desconectado")}",
            $"Ping: {relay.LatencyMs} ms",
            $"Espectadores: {relay.Viewers}",
            $"Reconexões: {relay.Reconnects}",
            $"Saída: {(config is null ? "—" : $"{config.Width}×{config.Height} @ {config.Fps} FPS")}",
            $"Codec: {video.Codec} {video.Profile} ({video.CodecString})",
            $"Encoder: {video.Encoder}",
            $"FPS real: {video.Fps:0.0}",
            $"Bitrate alvo: {(config is null ? "—" : $"{config.BitrateMbps} Mbps")}",
            $"Frames: {video.Frames}",
            $"Keyframes: {video.Keyframes}",
            $"Reinícios de encoder: {video.Restarts}",
            $"Vídeo enviado: {relay.VideoSent}",
            $"Vídeo descartado: {relay.VideoDropped}",
            $"Fila vídeo: {relay.VideoQueue}",
            $"Áudio enviado: {relay.AudioSent}",
            $"Áudio descartado: {relay.AudioDropped}",
            $"Fila áudio: {relay.AudioQueue}",
            $"Negociação: {_audience.ModeKey} · {_audience.VideoCodec}/{_audience.VideoProfile} · {_audience.Reason}",
            string.IsNullOrWhiteSpace(relay.LastError) ? "Último erro: —" : $"Último erro: {relay.LastError}"
        });
        MessageBox.Show(this, text, "Diagnóstico AKTela", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, ModControl | ModShift, VkS);
        if (!_hotkeyRegistered) _detail.Text = "Atalho Ctrl + Shift + S indisponível; use o botão ou a bandeja.";
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotkeyRegistered)
        {
            try { UnregisterHotKey(Handle, HotkeyId); } catch { }
            _hotkeyRegistered = false;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            _ = Toggle();
            return;
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void UpdateTray()
    {
        var status = _sharing
            ? (_relay.ViewerCount > 0 ? _status.Text : "Ligado · aguardando espectador")
            : (_publisherBlocked ? "Transmissão já em uso" : "Pronto");
        _trayStatusItem.Text = status;
        _tray.Text = _sharing
            ? (_relay.ViewerCount > 0 ? $"AKTela · {_relay.ViewerCount} assistindo" : "AKTela · aguardando espectador")
            : "AKTela Capture · pronto";
    }

    private void Restore()
    {
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Bounds = FitBoundsToWorkingArea(Screen.FromControl(this).WorkingArea, Bounds.Size);
        Activate();
        BringToFront();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1200, "AKTela continua ativo", "Clique com o botão esquerdo no ícone da bandeja para abrir.", ToolTipIcon.Info);
        }
        else
        {
            _adaptiveTimer.Stop();
            _adaptiveTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
    }

    private static string ShortEncoder(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        return value
            .Replace("NVENC · Desktop Duplication", "NVENC")
            .Replace("Media Foundation · Desktop Duplication", "Media F.")
            .Replace("Software H.264", "Software")
            .Replace("NVENC · D3D11 compatível", "NVENC")
            .Replace("NVENC · D3D11", "NVENC")
            .Replace("Media Foundation · D3D11", "Media F.")
            .Replace("NVENC · compatibilidade", "NVENC")
            .Replace("Media Foundation · compatibilidade", "Media F.")
            .Replace("Software H.264 · compatibilidade", "Software")
            .Replace("Software VP8 · D3D11", "VP8")
            .Replace("Software VP8 · compatibilidade", "VP8");
    }

    private void Ui(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed && !Disposing) action(); });
            else action();
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }
}
