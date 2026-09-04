using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ScreenCapture.NET;

namespace AKTelaCapture;

internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(20, 21, 25);
    private static readonly Color Surface = Color.FromArgb(29, 31, 37);
    private static readonly Color Surface2 = Color.FromArgb(37, 40, 48);
    private static readonly Color Surface3 = Color.FromArgb(45, 48, 57);
    private static readonly Color TextMain = Color.FromArgb(244, 245, 248);
    private static readonly Color TextMuted = Color.FromArgb(159, 165, 180);
    private static readonly Color Accent = Color.FromArgb(95, 106, 255);
    private static readonly Color Green = Color.FromArgb(67, 207, 148);
    private static readonly Color Yellow = Color.FromArgb(238, 180, 74);
    private static readonly Color Red = Color.FromArgb(239, 92, 105);

    private readonly DX11ScreenCaptureService _displayService = new();
    private readonly RelayClient _relay = new();
    private readonly VideoStreamer _video = new();
    private readonly AudioStreamer _audio = new();
    private readonly CursorTracker _cursor;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SemaphoreSlim _mediaGate = new(1, 1);

    private readonly TextBox _roomCodeBox = new();
    private readonly ComboBox _sourceTypeCombo = new();
    private readonly ComboBox _sourceCombo = new();
    private readonly ComboBox _qualityCombo = new();
    private readonly ComboBox _audioCombo = new();
    private readonly ComboBox _cursorCombo = new();
    private readonly CheckBox _minimizeCheck = new();
    private readonly CheckBox _audioCheck = new();
    private readonly ComboBox _profileCombo = new();
    private readonly Button _toggle = new();
    private readonly Label _status = new();
    private readonly Label _statusDetail = new();
    private readonly Panel _dot = new();
    private readonly NotifyIcon _tray = new();
    private readonly Dictionary<string, Button> _presetButtons = new(StringComparer.OrdinalIgnoreCase);
    private Label _outputValue = new(), _fpsValue = new(), _encoderValue = new(), _latencyValue = new();

    private bool _sharing, _connected, _allowClose, _syncingUi;
    private string _preset = "Jogo";
    private CaptureSourceOption? _activeSource;
    private StreamConfig? _activeConfig;
    private AudioCaptureMode _activeAudioMode = AudioCaptureMode.Off;

    public MainForm()
    {
        _cursor = new CursorTracker(_relay);
        Text = "AKTela Capture";
        ClientSize = new Size(390, 542);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = MaximumSize = Size;
        DoubleBuffered = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        BuildUi();
        BuildTray();
        HookEvents();
        RestoreSettings();
        RegisterGlobalHotkeys();

        Shown += (_, _) => ApplyRoundedWindow();
        Resize += (_, _) => ApplyRoundedWindow();
        FormClosing += OnFormClosing;
    }

    private void HookEvents()
    {
        _relay.ConnectionChanged += v => Ui(() => { _connected = v; RefreshStatus(); });
        _relay.ViewerCountChanged += v => { Ui(RefreshStatus); _ = SyncMediaAsync(v); };
        _relay.LatencyChanged += ms => Ui(() => { _latencyValue.Text = ms <= 0 ? "—" : $"{ms} ms"; RefreshStatusDetail(); });
        _relay.RelayError += _ => Ui(() => { if (_sharing && !_connected) SetStatus("Reconectando ao servidor", Yellow); });
        _video.PacketReady += p => _relay.TryQueuePacket(p);
        _video.FpsChanged += f => Ui(() => { _fpsValue.Text = f <= 0 ? "—" : $"{f:0} FPS"; RefreshStatusDetail(); });
        _video.EncoderChanged += e => Ui(() => { _encoderValue.Text = ShortEncoder(e); RefreshStatusDetail(); });
        _video.StreamError += e => Ui(() =>
        {
            SetStatus("Falha no encoder de vídeo", Red);
            MessageBox.Show(this, e, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        });
        _audio.PacketReady += p => _relay.TryQueuePacket(p);
        _audio.AudioError += e => Ui(() =>
        {
            SetStatus("Vídeo ativo, áudio indisponível", Yellow);
            MessageBox.Show(this, e, "Áudio do AKTela", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });

        _profileCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingUi || _sharing) return;
            var name = _profileCombo.SelectedItem?.ToString() ?? "Personalizado";
            if (name is "Jogo" or "Filme" or "Leve") ApplyPreset(name);
            else SetPresetVisual("Personalizado");
        };
        _audioCheck.CheckedChanged += (_, _) =>
        {
            if (_syncingUi || _sharing) return;
            if (_audioCheck.Checked)
            {
                var sourceIsWindow = (_sourceTypeCombo.SelectedItem?.ToString() ?? "Janela") == "Janela";
                SelectAudio(sourceIsWindow ? AudioCaptureMode.SourceOnly : AudioCaptureMode.SystemWithoutDiscord);
            }
            else SelectAudio(AudioCaptureMode.Off);
            MarkCustomIfUserChanged();
        };

        _sourceTypeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_sharing)
            {
                LoadSources();
                PopulateAudioOptions(preferRecommended: true);
                MarkCustomIfUserChanged();
            }
        };
        _sourceCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_sharing) PopulateAudioOptions(preferRecommended: false);
        };
        _qualityCombo.SelectedIndexChanged += (_, _) => MarkCustomIfUserChanged();
        _audioCombo.SelectedIndexChanged += (_, _) => MarkCustomIfUserChanged();
        _cursorCombo.SelectedIndexChanged += (_, _) => MarkCustomIfUserChanged();
    }

    private void BuildUi()
    {
        var drag = new Panel { Bounds = new Rectangle(0, 0, 390, 52), BackColor = Bg };
        drag.MouseDown += DragWindow;
        Controls.Add(drag);

        var logo = new Panel { Bounds = new Rectangle(18, 12, 28, 28), BackColor = Accent };
        logo.Paint += (_, e) => TextRenderer.DrawText(
            e.Graphics, "AK", new Font("Segoe UI", 8.2F, FontStyle.Bold),
            logo.ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        drag.Controls.Add(logo); RoundControl(logo, 8);

        var title = new Label
        {
            Text = "AKTela Capture",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            ForeColor = TextMain,
            Location = new Point(57, 17)
        };
        title.MouseDown += DragWindow;
        drag.Controls.Add(title);

        var options = TitleButton("•••", 286);
        options.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
        options.Click += (_, _) => ShowOptionsMenu(options);
        drag.Controls.Add(options);

        var min = TitleButton("—", 321);
        min.Click += (_, _) => HideToTray();
        drag.Controls.Add(min);

        var close = TitleButton("×", 355);
        close.Click += (_, _) => HideToTray();
        drag.Controls.Add(close);

        var statusCard = CardPanel(24, 62, 342, 58);
        Controls.Add(statusCard);
        _dot.Bounds = new Rectangle(16, 17, 9, 9);
        _dot.BackColor = TextMuted;
        _dot.Paint += (_, e) =>
        {
            using var b = new SolidBrush(_dot.BackColor);
            e.Graphics.FillEllipse(b, 0, 0, 9, 9);
        };
        statusCard.Controls.Add(_dot);

        _status.Text = "Pronto para compartilhar";
        _status.AutoSize = true;
        _status.Font = new Font("Segoe UI Semibold", 9.2F, FontStyle.Bold);
        _status.ForeColor = TextMain;
        _status.Location = new Point(36, 11);
        statusCard.Controls.Add(_status);

        _statusDetail.Text = "Configure a transmissão e clique em iniciar";
        _statusDetail.AutoSize = true;
        _statusDetail.Font = new Font("Segoe UI", 7.8F);
        _statusDetail.ForeColor = TextMuted;
        _statusDetail.Location = new Point(36, 33);
        statusCard.Controls.Add(_statusDetail);

        LabelAt("Código da Activity", 25, 137);
        _roomCodeBox.Bounds = new Rectangle(24, 159, 342, 34);
        _roomCodeBox.BackColor = Surface2;
        _roomCodeBox.ForeColor = TextMain;
        _roomCodeBox.BorderStyle = BorderStyle.FixedSingle;
        _roomCodeBox.CharacterCasing = CharacterCasing.Upper;
        _roomCodeBox.MaxLength = 6;
        _roomCodeBox.TextAlign = HorizontalAlignment.Center;
        _roomCodeBox.Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Bold);
        Controls.Add(_roomCodeBox);

        LabelAt("Modo", 25, 210);
        LabelAt("Qualidade", 202, 210);

        _profileCombo.Bounds = new Rectangle(24, 232, 160, 32);
        StyleCombo(_profileCombo);
        _profileCombo.Items.AddRange(["Jogo", "Filme", "Leve", "Personalizado"]);
        Controls.Add(_profileCombo);

        _qualityCombo.Bounds = new Rectangle(201, 232, 165, 32);
        StyleCombo(_qualityCombo);
        foreach (var q in QualityOption.All) _qualityCombo.Items.Add(q);
        Controls.Add(_qualityCombo);

        LabelAt("Compartilhar", 25, 282);
        _sourceTypeCombo.Bounds = new Rectangle(24, 304, 96, 32);
        StyleCombo(_sourceTypeCombo);
        _sourceTypeCombo.Items.AddRange(["Tela", "Janela"]);
        Controls.Add(_sourceTypeCombo);

        _sourceCombo.Bounds = new Rectangle(127, 304, 202, 32);
        StyleCombo(_sourceCombo);
        Controls.Add(_sourceCombo);

        var refresh = new Button
        {
            Text = "↻",
            Bounds = new Rectangle(336, 304, 30, 32),
            BackColor = Surface2,
            ForeColor = TextMuted,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Symbol", 11F),
            TabStop = false
        };
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => LoadSources();
        Controls.Add(refresh);
        RoundControl(refresh, 8);

        var audioRow = CardPanel(24, 354, 342, 58);
        Controls.Add(audioRow);
        _audioCheck.Text = "Compartilhar áudio";
        _audioCheck.AutoSize = true;
        _audioCheck.ForeColor = TextMain;
        _audioCheck.BackColor = Surface;
        _audioCheck.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        _audioCheck.Location = new Point(14, 10);
        audioRow.Controls.Add(_audioCheck);
        audioRow.Controls.Add(new Label
        {
            Text = "O áudio do Discord é removido automaticamente.",
            AutoSize = true,
            ForeColor = TextMuted,
            BackColor = Surface,
            Font = new Font("Segoe UI", 7.6F),
            Location = new Point(15, 34)
        });

        // Controles internos: continuam existindo para preservar a lógica,
        // mas as opções técnicas ficam fora da interface principal.
        _audioCombo.Visible = false;
        _cursorCombo.Visible = false;
        _cursorCombo.Items.AddRange(["Automático", "Mostrar", "Ocultar"]);

        _toggle.Bounds = new Rectangle(24, 432, 342, 56);
        _toggle.Text = "Iniciar transmissão";
        _toggle.BackColor = Accent;
        _toggle.ForeColor = Color.White;
        _toggle.FlatStyle = FlatStyle.Flat;
        _toggle.FlatAppearance.BorderSize = 0;
        _toggle.Font = new Font("Segoe UI Semibold", 10.2F, FontStyle.Bold);
        _toggle.Cursor = Cursors.Hand;
        _toggle.Click += async (_, _) => await ToggleAsync();
        Controls.Add(_toggle);
        RoundControl(_toggle, 13);

        Controls.Add(new Label
        {
            Text = "Ctrl + Shift + S inicia ou encerra",
            Bounds = new Rectangle(24, 508, 342, 18),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(116, 122, 136),
            Font = new Font("Segoe UI", 7.2F)
        });

        _minimizeCheck.Visible = false;
    }

    private void ShowOptionsMenu(Control anchor)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Surface2,
            ForeColor = TextMain,
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer()
        };

        var cursorMenu = new ToolStripMenuItem("Cursor") { ForeColor = TextMain };
        foreach (var item in new[] { "Automático", "Mostrar", "Ocultar" })
        {
            var entry = new ToolStripMenuItem(item)
            {
                Checked = string.Equals(_cursorCombo.SelectedItem?.ToString(), item, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false
            };
            entry.Click += (_, _) =>
            {
                _cursorCombo.SelectedItem = item;
                SetPresetVisual("Personalizado");
            };
            cursorMenu.DropDownItems.Add(entry);
        }

        var minimize = new ToolStripMenuItem("Minimizar após iniciar")
        {
            Checked = _minimizeCheck.Checked,
            CheckOnClick = true
        };
        minimize.CheckedChanged += (_, _) => _minimizeCheck.Checked = minimize.Checked;

        var shortcut = new ToolStripMenuItem("Atalho: Ctrl + Shift + S") { Enabled = false };

        menu.Items.Add(cursorMenu);
        menu.Items.Add(minimize);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(shortcut);
        menu.Show(anchor, new Point(anchor.Width - menu.Width, anchor.Height));
    }

    private void RestoreSettings()
    {
        _roomCodeBox.Text = RelayClient.NormalizeRoomCode(_settings.RoomCode);
        _minimizeCheck.Checked = _settings.MinimizeAfterStart;
        _sourceTypeCombo.SelectedItem = _settings.SourceType is "Tela" or "Janela" ? _settings.SourceType : "Janela";
        LoadSources();

        var quality = QualityOption.All.FirstOrDefault(q => q.Key == _settings.Quality) ?? QualityOption.All[^1];
        _qualityCombo.SelectedItem = quality;
        _cursorCombo.SelectedItem = _settings.CursorPolicy switch { "Mostrar" => "Mostrar", "Ocultar" => "Ocultar", _ => "Automático" };
        PopulateAudioOptions(preferRecommended: false);
        SelectAudioFromSettings();
        SetPresetVisual(_settings.Preset is "Jogo" or "Filme" or "Leve" or "Personalizado" ? _settings.Preset : "Jogo");
        _audioCheck.Checked = (_audioCombo.SelectedItem as AudioOption)?.Mode != AudioCaptureMode.Off;
    }

    private void ApplyPreset(string name)
    {
        _preset = name;
        if (name == "Jogo")
        {
            _sourceTypeCombo.SelectedItem = "Janela";
            SelectQuality("1080p60");
            _cursorCombo.SelectedItem = "Automático";
            SelectAudio(AudioCaptureMode.SourceOnly);
        }
        else if (name == "Filme")
        {
            _sourceTypeCombo.SelectedItem = "Janela";
            SelectQuality("1080p30");
            _cursorCombo.SelectedItem = "Automático";
            SelectAudio(AudioCaptureMode.SourceOnly);
        }
        else if (name == "Leve")
        {
            _sourceTypeCombo.SelectedItem = "Tela";
            SelectQuality("720p30");
            _cursorCombo.SelectedItem = "Automático";
            SelectAudio(AudioCaptureMode.SystemWithoutDiscord);
        }
        SetPresetVisual(name);
    }

    private void MarkCustomIfUserChanged()
    {
        if (_sharing || !IsHandleCreated || _preset == "Personalizado") return;
        // Mudanças manuais fora da aplicação de um preset passam a ser Personalizado.
        if (Focused || _sourceTypeCombo.Focused || _qualityCombo.Focused || _audioCheck.Focused || _audioCombo.Focused || _cursorCombo.Focused)
            SetPresetVisual("Personalizado");
    }

    private void SetPresetVisual(string name)
    {
        _preset = name;
        foreach (var kv in _presetButtons)
        {
            var selected = kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase);
            kv.Value.BackColor = selected ? Accent : Surface2;
            kv.Value.ForeColor = selected ? Color.White : TextMuted;
        }

        _syncingUi = true;
        try
        {
            if (_profileCombo.Items.Contains(name))
                _profileCombo.SelectedItem = name;
        }
        finally { _syncingUi = false; }
    }

    private void SelectQuality(string key)
    {
        var item = QualityOption.All.First(q => q.Key == key);
        _qualityCombo.SelectedItem = item;
    }

    private void LoadSources()
    {
        var previousLabel = (_sourceCombo.SelectedItem as CaptureSourceOption)?.Label;
        _sourceCombo.Items.Clear();
        var type = _sourceTypeCombo.SelectedItem?.ToString() ?? "Janela";

        if (type == "Tela")
        {
            try
            {
                var screens = Screen.AllScreens;
                var n = 1; var ff = 0;
                foreach (var card in _displayService.GetGraphicsCards())
                {
                    foreach (var display in _displayService.GetDisplays(card))
                    {
                        var bounds = ff < screens.Length ? screens[ff].Bounds : new Rectangle(0, 0, display.Width, display.Height);
                        _sourceCombo.Items.Add(new CaptureSourceOption
                        {
                            Kind = CaptureSourceKind.Display,
                            Label = $"Tela {n} · {display.Width} × {display.Height}",
                            Width = display.Width,
                            Height = display.Height,
                            ScreenBounds = bounds,
                            FfmpegOutputIndex = ff,
                            Display = display
                        });
                        n++; ff++;
                    }
                }
            }
            catch { }
        }
        else
        {
            foreach (var window in WindowEnumerator.GetWindows()) _sourceCombo.Items.Add(window);
        }

        if (_sourceCombo.Items.Count == 0)
        {
            SetStatus(type == "Tela" ? "Nenhuma tela encontrada" : "Nenhuma janela encontrada", Red);
            _toggle.Enabled = false;
            return;
        }

        _toggle.Enabled = true;
        var restore = _sourceCombo.Items.Cast<CaptureSourceOption>().FirstOrDefault(x => x.Label == previousLabel);
        _sourceCombo.SelectedItem = restore ?? _sourceCombo.Items[0];
        if (!_sharing) RefreshStatus();
    }

    private void PopulateAudioOptions(bool preferRecommended)
    {
        var current = (_audioCombo.SelectedItem as AudioOption)?.Mode;
        _audioCombo.Items.Clear();
        var sourceIsWindow = (_sourceTypeCombo.SelectedItem?.ToString() ?? "Janela") == "Janela";
        if (sourceIsWindow)
            _audioCombo.Items.Add(new AudioOption(AudioCaptureMode.SourceOnly, "Somente da janela selecionada · recomendado"));
        _audioCombo.Items.Add(new AudioOption(AudioCaptureMode.SystemWithoutDiscord, "Sistema sem áudio do Discord · recomendado"));
        _audioCombo.Items.Add(new AudioOption(AudioCaptureMode.SystemAll, "Sistema inteiro · pode incluir vozes do Discord"));
        _audioCombo.Items.Add(new AudioOption(AudioCaptureMode.Off, "Sem áudio"));

        var recommended = sourceIsWindow ? AudioCaptureMode.SourceOnly : AudioCaptureMode.SystemWithoutDiscord;
        var target = current == AudioCaptureMode.Off
            ? AudioCaptureMode.Off
            : preferRecommended ? recommended : current ?? recommended;
        SelectAudio(target);
    }

    private void SelectAudioFromSettings()
    {
        var mode = _settings.AudioMode switch
        {
            "SistemaSemDiscord" => AudioCaptureMode.SystemWithoutDiscord,
            "Sistema" => AudioCaptureMode.SystemAll,
            "Desligado" => AudioCaptureMode.Off,
            _ => AudioCaptureMode.SourceOnly
        };
        SelectAudio(mode);
    }

    private void SelectAudio(AudioCaptureMode mode)
    {
        var option = _audioCombo.Items.Cast<AudioOption>().FirstOrDefault(x => x.Mode == mode)
                     ?? _audioCombo.Items.Cast<AudioOption>().FirstOrDefault();
        if (option is not null)
        {
            _audioCombo.SelectedItem = option;
            _syncingUi = true;
            try { _audioCheck.Checked = option.Mode != AudioCaptureMode.Off; }
            finally { _syncingUi = false; }
        }
    }

    private async Task ToggleAsync()
    {
        if (_sharing) { await StopSharingAsync(); return; }
        if (_sourceCombo.SelectedItem is not CaptureSourceOption source) return;
        if (_qualityCombo.SelectedItem is not QualityOption quality) return;
        if (_audioCombo.SelectedItem is not AudioOption audioOption) return;

        var room = RelayClient.NormalizeRoomCode(_roomCodeBox.Text);
        if (room.Length != 6)
        {
            MessageBox.Show(this, "Cole o código de 6 caracteres mostrado na AKTela dentro do Discord.", "Código da Activity", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var cursorPolicy = _cursorCombo.SelectedItem?.ToString() ?? "Automático";
        var config = new StreamConfig(
            quality.Width, quality.Height, quality.Fps, quality.BitrateMbps,
            audioOption.Mode != AudioCaptureMode.Off,
            _preset,
            source.Kind == CaptureSourceKind.Window ? "Janela" : "Tela",
            audioOption.Label,
            cursorPolicy);

        _activeSource = source; _activeConfig = config; _activeAudioMode = audioOption.Mode;
        SaveSettings(room, quality, audioOption.Mode, cursorPolicy);
        _toggle.Enabled = false; SetStatus("Conectando ao servidor", Yellow);

        try
        {
            await _relay.StartAsync(room, config);
            _sharing = true;
            LockInputs(true);
            _toggle.Text = "Encerrar transmissão"; _toggle.BackColor = Red;
            _outputValue.Text = config.ResolutionLabel;
            RefreshStatus();
            if (_relay.ViewerCount > 0) await SyncMediaAsync(_relay.ViewerCount);
            if (_minimizeCheck.Checked) BeginInvoke((Action)HideToTray);
        }
        catch (Exception ex)
        {
            await _relay.StopAsync();
            MessageBox.Show(this, ex.Message, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Não foi possível conectar", Red);
        }
        finally { _toggle.Enabled = true; }
    }

    private async Task SyncMediaAsync(int viewers)
    {
        await _mediaGate.WaitAsync();
        try
        {
            if (!_sharing) return;
            if (viewers <= 0)
            {
                _cursor.Stop();
                await _video.StopAsync();
                await _audio.StopAsync();
                Ui(() => { _fpsValue.Text = "—"; _encoderValue.Text = "—"; RefreshStatus(); });
                return;
            }
            if (_video.IsRunning) return;
            var source = _activeSource; var cfg = _activeConfig;
            if (source is null || cfg is null) return;

            if (!File.Exists(FfmpegManager.FfmpegPath))
            {
                Ui(() => SetStatus("Preparando encoder · 0%", Yellow));
                var progress = new Progress<int>(p => Ui(() => SetStatus($"Preparando encoder · {p}%", Yellow)));
                await FfmpegManager.EnsureAsync(progress);
            }

            await _video.StartAsync(source, cfg);
            if (_activeAudioMode != AudioCaptureMode.Off)
                await _audio.StartAsync(_activeAudioMode, source.ProcessId);

            _cursor.Start(source, ShouldShowCursor);
            Ui(RefreshStatus);
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                SetStatus("Falha ao iniciar mídia", Red);
                MessageBox.Show(this, ex.Message, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
        }
        finally { _mediaGate.Release(); }
    }

    private bool ShouldShowCursor()
    {
        var policy = _activeConfig?.CursorPolicy ?? "Automático";
        if (policy == "Mostrar") return true;
        if (policy == "Ocultar") return false;
        return !string.Equals(_activeConfig?.PresetName, "Jogo", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StopSharingAsync()
    {
        _toggle.Enabled = false; _sharing = false;
        _cursor.Stop(); await _video.StopAsync(); await _audio.StopAsync(); await _relay.StopAsync();
        _connected = false; _activeSource = null; _activeConfig = null;
        LockInputs(false); _toggle.Text = "Iniciar transmissão"; _toggle.BackColor = Accent;
        _outputValue.Text = "—"; _fpsValue.Text = "—"; _encoderValue.Text = "—"; _latencyValue.Text = "—";
        SetStatus("Pronto para compartilhar", TextMuted); _statusDetail.Text = "Configure a transmissão e clique em iniciar"; _toggle.Enabled = true;
    }

    private void SaveSettings(string room, QualityOption quality, AudioCaptureMode audioMode, string cursorPolicy)
    {
        _settings.RoomCode = room;
        _settings.Preset = _preset;
        _settings.Quality = quality.Key;
        _settings.SourceType = _sourceTypeCombo.SelectedItem?.ToString() ?? "Janela";
        _settings.CursorPolicy = cursorPolicy switch { "Mostrar" => "Mostrar", "Ocultar" => "Ocultar", _ => "Auto" };
        _settings.AudioMode = audioMode switch
        {
            AudioCaptureMode.SystemWithoutDiscord => "SistemaSemDiscord",
            AudioCaptureMode.SystemAll => "Sistema",
            AudioCaptureMode.Off => "Desligado",
            _ => "Fonte"
        };
        _settings.MinimizeAfterStart = _minimizeCheck.Checked;
        _settings.Save();
    }

    private void RefreshStatus()
    {
        if (!_sharing)
        {
            SetStatus("Pronto para compartilhar", TextMuted);
            _statusDetail.Text = "Configure a transmissão e clique em iniciar";
            return;
        }
        if (!_connected)
        {
            SetStatus("Conectando ao servidor", Yellow);
            _statusDetail.Text = "Preparando uma conexão de baixa latência";
            return;
        }
        if (_relay.ViewerCount == 0)
        {
            SetStatus("Ligado · aguardando espectador", Green);
            RefreshStatusDetail();
            return;
        }

        SetStatus($"Ao vivo · {_relay.ViewerCount} assistindo", _relay.LatencyMs >= 160 ? Yellow : Green);
        RefreshStatusDetail();
    }

    private void RefreshStatusDetail()
    {
        if (!_sharing) return;
        var parts = new List<string>();
        if (_activeConfig is not null)
            parts.Add($"{_activeConfig.ResolutionLabel} · {_activeConfig.Fps} FPS");
        if (_encoderValue.Text is not "—" and not "")
            parts.Add(_encoderValue.Text);
        if (_relay.LatencyMs > 0)
            parts.Add($"{_relay.LatencyMs} ms");
        _statusDetail.Text = parts.Count > 0 ? string.Join("  ·  ", parts) : "Transmissão pronta";
    }

    private void LockInputs(bool locked)
    {
        _roomCodeBox.Enabled = !locked; _sourceTypeCombo.Enabled = !locked; _sourceCombo.Enabled = !locked;
        _qualityCombo.Enabled = !locked; _audioCombo.Enabled = !locked; _cursorCombo.Enabled = !locked;
        _profileCombo.Enabled = !locked; _audioCheck.Enabled = !locked;
        foreach (var button in _presetButtons.Values) button.Enabled = !locked;
    }

    private void BuildTray()
    {
        _tray.Icon = Icon; _tray.Text = "AKTela Capture"; _tray.Visible = true;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Iniciar / encerrar", null, async (_, _) => await ToggleAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, async (_, _) => { _allowClose = true; if (_sharing) await StopSharingAsync(); Close(); });
        _tray.ContextMenuStrip = menu;
    }

    private void RegisterGlobalHotkeys()
    {
        HandleCreated += (_, _) => RegisterHotKey(Handle, 0xA71, 0x0002 | 0x0004, (uint)Keys.S);
        HandleDestroyed += (_, _) => UnregisterHotKey(Handle, 0xA71);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == 0xA71)
        {
            _ = ToggleAsync();
            return;
        }
        base.WndProc(ref m);
    }

    private void HideToTray()
    {
        Hide();
        if (_sharing) _tray.Text = $"AKTela Capture · {_relay.ViewerCount} assistindo";
    }

    private void RestoreFromTray()
    {
        Show(); WindowState = FormWindowState.Normal; Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; HideToTray(); }
        else
        {
            _tray.Visible = false;
            _cursor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _displayService.Dispose(); _mediaGate.Dispose();
        }
    }

    private Panel CardPanel(int x, int y, int w, int h)
    {
        var p = new Panel { Bounds = new Rectangle(x, y, w, h), BackColor = Surface };
        p.Paint += PaintRoundedPanel; return p;
    }

    private void LabelAt(string text, int x, int y) => Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = TextMuted, Location = new Point(x, y) });

    private void StyleCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Surface2; combo.ForeColor = TextMain; combo.Font = new Font("Segoe UI", 9.4F);
    }

    private void AddMetric(Panel p, string title, string value, int x, int y, out Label label)
    {
        p.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = TextMuted, Location = new Point(x, y), Font = new Font("Segoe UI", 7.3F) });
        label = new Label { Text = value, AutoSize = true, ForeColor = TextMain, Location = new Point(x, y + 25), Font = new Font("Segoe UI Semibold", 8.2F, FontStyle.Bold) };
        p.Controls.Add(label);
    }

    private Button TitleButton(string text, int x)
    {
        var b = new Button { Text = text, Bounds = new Rectangle(x, 12, 30, 30), BackColor = Bg, ForeColor = TextMuted, FlatStyle = FlatStyle.Flat, TabStop = false, Cursor = Cursors.Hand, Font = new Font("Segoe UI", text == "×" ? 13F : 10F) };
        b.FlatAppearance.BorderSize = 0; return b;
    }

    private static string ShortEncoder(string value) => value
        .Replace("NVIDIA NVENC (compatibilidade)", "NVENC compat.")
        .Replace("NVIDIA NVENC", "NVENC")
        .Replace("Media Foundation", "Media Foundation");

    private void SetStatus(string text, Color color) { _status.Text = text; _dot.BackColor = color; _dot.Invalidate(); }
    private void Ui(Action action) { if (IsDisposed) return; try { if (InvokeRequired) BeginInvoke(action); else action(); } catch { } }
    private static void PaintRoundedPanel(object? sender, PaintEventArgs e) { if (sender is not Panel p) return; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var path = RoundedRect(p.ClientRectangle, 12); using var brush = new SolidBrush(p.BackColor); e.Graphics.FillPath(brush, path); }
    private static GraphicsPath RoundedRect(Rectangle r, int radius) { var d = radius * 2; var p = new GraphicsPath(); p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
    private static void RoundControl(Control c, int radius) { using var p = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius); c.Region = new Region(p); }
    private void ApplyRoundedWindow() { using var p = RoundedRect(new Rectangle(0, 0, Width, Height), 18); Region = new Region(p); }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wp, int lp);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0);
    }
}
