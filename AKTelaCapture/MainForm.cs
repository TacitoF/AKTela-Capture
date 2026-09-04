using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ScreenCapture.NET;

namespace AKTelaCapture;

internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(8, 13, 22);
    private static readonly Color Surface = Color.FromArgb(15, 23, 35);
    private static readonly Color Surface2 = Color.FromArgb(20, 31, 47);
    private static readonly Color Surface3 = Color.FromArgb(28, 42, 61);
    private static readonly Color Stroke = Color.FromArgb(42, 61, 84);
    private static readonly Color TextMain = Color.FromArgb(246, 249, 253);
    private static readonly Color TextMuted = Color.FromArgb(139, 155, 178);
    private static readonly Color Accent = Color.FromArgb(32, 169, 255);
    private static readonly Color AccentPressed = Color.FromArgb(25, 132, 222);
    private static readonly Color Green = Color.FromArgb(73, 209, 157);
    private static readonly Color Yellow = Color.FromArgb(239, 184, 78);
    private static readonly Color Red = Color.FromArgb(242, 87, 104);

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
    private readonly ToolStripMenuItem _trayStatusItem = new();
    private readonly ToolStripMenuItem _trayToggleItem = new();
    private readonly Dictionary<string, Button> _presetButtons = new(StringComparer.OrdinalIgnoreCase);
    private Label _outputValue = new(), _fpsValue = new(), _encoderValue = new(), _latencyValue = new();

    private bool _sharing, _connected, _allowClose, _syncingUi, _trayHintShown;
    private string _preset = "Jogo";
    private CaptureSourceOption? _activeSource;
    private StreamConfig? _activeConfig;
    private AudioCaptureMode _activeAudioMode = AudioCaptureMode.Off;

    public MainForm()
    {
        _cursor = new CursorTracker(_relay);
        Text = "AKTela Capture";
        ClientSize = new Size(420, 566);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI Variable Text", 9F);
        MinimumSize = MaximumSize = Size;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Icon = AppIcon.Load();
        Paint += PaintWindowBorder;

        BuildUi();
        BuildTray();
        HookEvents();
        RestoreSettings();
        RegisterGlobalHotkeys();

        Shown += (_, _) => { ApplyRoundedWindow(); ApplyDwmAttributes(); };
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
            UpdateAudioButtonVisual();
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
        SuspendLayout();

        var drag = new Panel { Bounds = new Rectangle(0, 0, 420, 56), BackColor = Bg };
        drag.MouseDown += DragWindow;
        Controls.Add(drag);

        var appIcon = new PictureBox
        {
            Bounds = new Rectangle(18, 11, 34, 34),
            Image = Icon?.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            TabStop = false
        };
        drag.Controls.Add(appIcon);

        var title = new Label
        {
            Text = "AKTela Capture",
            AutoSize = true,
            Font = new Font("Segoe UI Variable Display", 10.5F, FontStyle.Bold),
            ForeColor = TextMain,
            Location = new Point(63, 9)
        };
        title.MouseDown += DragWindow;
        drag.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Compartilhamento leve",
            AutoSize = true,
            Font = new Font("Segoe UI Variable Text", 7.7F),
            ForeColor = TextMuted,
            Location = new Point(64, 31)
        };
        subtitle.MouseDown += DragWindow;
        drag.Controls.Add(subtitle);

        var options = TitleButton("⋯", 316);
        options.Font = new Font("Segoe UI Symbol", 11F, FontStyle.Bold);
        options.Click += (_, _) => ShowOptionsMenu(options);
        drag.Controls.Add(options);

        var min = TitleButton("—", 350);
        min.Click += (_, _) => HideToTray();
        drag.Controls.Add(min);

        var close = TitleButton("×", 384);
        close.Click += (_, _) => HideToTray();
        drag.Controls.Add(close);

        var statusCard = CardPanel(18, 64, 384, 50);
        Controls.Add(statusCard);
        _dot.Bounds = new Rectangle(15, 20, 8, 8);
        _dot.BackColor = TextMuted;
        _dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_dot.BackColor);
            e.Graphics.FillEllipse(b, 0, 0, 8, 8);
        };
        statusCard.Controls.Add(_dot);

        _status.Text = "Pronto para compartilhar";
        _status.AutoSize = true;
        _status.Font = new Font("Segoe UI Variable Text", 9F, FontStyle.Bold);
        _status.ForeColor = TextMain;
        _status.Location = new Point(34, 8);
        statusCard.Controls.Add(_status);

        _statusDetail.Text = "Cole o código e escolha o que compartilhar";
        _statusDetail.AutoSize = true;
        _statusDetail.Font = new Font("Segoe UI Variable Text", 7.5F);
        _statusDetail.ForeColor = TextMuted;
        _statusDetail.Location = new Point(34, 27);
        statusCard.Controls.Add(_statusDetail);

        LabelAt("Código da Activity", 18, 130);
        var codeWrap = CardPanel(18, 151, 384, 40, Surface2, 10);
        Controls.Add(codeWrap);
        _roomCodeBox.Bounds = new Rectangle(12, 8, 278, 24);
        _roomCodeBox.BackColor = Surface2;
        _roomCodeBox.ForeColor = TextMain;
        _roomCodeBox.BorderStyle = BorderStyle.None;
        _roomCodeBox.CharacterCasing = CharacterCasing.Upper;
        _roomCodeBox.MaxLength = 6;
        _roomCodeBox.TextAlign = HorizontalAlignment.Left;
        _roomCodeBox.Font = new Font("Segoe UI Variable Text", 11F, FontStyle.Bold);
        _roomCodeBox.PlaceholderText = "ABC123";
        _roomCodeBox.Click += (_, _) => _roomCodeBox.SelectAll();
        codeWrap.Controls.Add(_roomCodeBox);

        var paste = SmallActionButton("Colar", new Rectangle(300, 5, 76, 30));
        paste.Click += (_, _) => PasteRoomCode();
        codeWrap.Controls.Add(paste);

        LabelAt("Modo", 18, 209);
        var presetBar = CardPanel(18, 230, 384, 40, Surface, 10);
        Controls.Add(presetBar);
        AddPresetButton(presetBar, "Jogo", 4, 4, 122, 32);
        AddPresetButton(presetBar, "Filme", 131, 4, 122, 32);
        AddPresetButton(presetBar, "Leve", 258, 4, 122, 32);

        // Mantido internamente para preservar a lógica de presets sem poluir a interface.
        _profileCombo.Visible = false;
        _profileCombo.Items.AddRange(["Jogo", "Filme", "Leve", "Personalizado"]);

        LabelAt("Qualidade", 18, 287);
        LabelAt("Fonte", 215, 287);

        _qualityCombo.Bounds = new Rectangle(18, 308, 184, 34);
        StyleCombo(_qualityCombo);
        foreach (var q in QualityOption.All) _qualityCombo.Items.Add(q);
        Controls.Add(_qualityCombo);

        _sourceTypeCombo.Bounds = new Rectangle(215, 308, 187, 34);
        StyleCombo(_sourceTypeCombo);
        _sourceTypeCombo.Items.AddRange(["Tela", "Janela"]);
        Controls.Add(_sourceTypeCombo);

        LabelAt("Janela ou tela", 18, 359);
        _sourceCombo.Bounds = new Rectangle(18, 380, 344, 34);
        StyleCombo(_sourceCombo);
        Controls.Add(_sourceCombo);

        var refresh = SmallActionButton("↻", new Rectangle(368, 380, 34, 34));
        refresh.Font = new Font("Segoe UI Symbol", 11F);
        refresh.Click += (_, _) => LoadSources();
        Controls.Add(refresh);

        var audioRow = CardPanel(18, 431, 384, 48);
        Controls.Add(audioRow);
        audioRow.Controls.Add(new Label
        {
            Text = "Áudio do sistema",
            AutoSize = true,
            ForeColor = TextMain,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Text", 8.8F, FontStyle.Bold),
            Location = new Point(14, 7)
        });
        audioRow.Controls.Add(new Label
        {
            Text = "Evita retorno do áudio do Discord",
            AutoSize = true,
            ForeColor = TextMuted,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Text", 7.2F),
            Location = new Point(14, 27)
        });

        _audioCheck.Appearance = Appearance.Button;
        _audioCheck.Bounds = new Rectangle(298, 9, 72, 30);
        _audioCheck.FlatStyle = FlatStyle.Flat;
        _audioCheck.FlatAppearance.BorderSize = 0;
        _audioCheck.TextAlign = ContentAlignment.MiddleCenter;
        _audioCheck.Font = new Font("Segoe UI Variable Text", 8F, FontStyle.Bold);
        _audioCheck.Cursor = Cursors.Hand;
        audioRow.Controls.Add(_audioCheck);
        RoundControl(_audioCheck, 9);

        // Opções técnicas continuam existindo, mas ficam no menu de opções.
        _audioCombo.Visible = false;
        _cursorCombo.Visible = false;
        _cursorCombo.Items.AddRange(["Automático", "Mostrar", "Ocultar"]);
        _minimizeCheck.Visible = false;

        _toggle.Bounds = new Rectangle(18, 497, 384, 52);
        _toggle.Text = "Iniciar transmissão";
        _toggle.BackColor = Accent;
        _toggle.ForeColor = Color.White;
        _toggle.FlatStyle = FlatStyle.Flat;
        _toggle.FlatAppearance.BorderSize = 0;
        _toggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 180, 255);
        _toggle.FlatAppearance.MouseDownBackColor = AccentPressed;
        _toggle.Font = new Font("Segoe UI Variable Text", 9.7F, FontStyle.Bold);
        _toggle.Cursor = Cursors.Hand;
        _toggle.Click += async (_, _) => await ToggleAsync();
        Controls.Add(_toggle);
        RoundControl(_toggle, 12);

        UpdateAudioButtonVisual();
        ResumeLayout(false);
    }

    private void ShowOptionsMenu(Control anchor)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI Variable Text", 9F)
        };

        var cursorMenu = new ToolStripMenuItem("Cursor");
        foreach (var item in new[] { "Automático", "Mostrar", "Ocultar" })
        {
            var entry = new ToolStripMenuItem(item)
            {
                Checked = string.Equals(_cursorCombo.SelectedItem?.ToString(), item, StringComparison.OrdinalIgnoreCase)
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

        var topMost = new ToolStripMenuItem("Manter janela acima")
        {
            Checked = TopMost,
            CheckOnClick = true
        };
        topMost.CheckedChanged += (_, _) => TopMost = topMost.Checked;

        menu.Items.Add(cursorMenu);
        menu.Items.Add(minimize);
        menu.Items.Add(topMost);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Atalho: Ctrl + Shift + S") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair do AKTela Capture", null, async (_, _) =>
        {
            _allowClose = true;
            if (_sharing) await StopSharingAsync();
            Close();
        });

        menu.Show(anchor, new Point(anchor.Width, anchor.Height));
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
        UpdateAudioButtonVisual();
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
                            CaptureDisplayBounds = bounds,
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
        SetStatus("Pronto para compartilhar", TextMuted); _statusDetail.Text = "Cole o código e escolha o que compartilhar"; _toggle.Enabled = true;
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
            _statusDetail.Text = "Cole o código e escolha o que compartilhar";
            UpdateTrayState();
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
        UpdateTrayState();
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
        _tray.Icon = Icon;
        _tray.Text = "AKTela Capture";
        _tray.Visible = true;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RestoreFromTray();
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _trayStatusItem.Enabled = false;
        _trayStatusItem.Text = "Pronto";
        _trayToggleItem.Text = "Iniciar transmissão";
        _trayToggleItem.Click += async (_, _) =>
        {
            if (!_sharing) RestoreFromTray();
            await ToggleAsync();
        };

        var menu = new ContextMenuStrip { Font = new Font("Segoe UI Variable Text", 9F) };
        menu.Opening += (_, _) => UpdateTrayState();
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir AKTela", null, (_, _) => RestoreFromTray());
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, async (_, _) =>
        {
            _allowClose = true;
            if (_sharing) await StopSharingAsync();
            Close();
        });
        _tray.ContextMenuStrip = menu;
        UpdateTrayState();
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
        UpdateTrayState();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.BalloonTipTitle = "AKTela continua em segundo plano";
            _tray.BalloonTipText = "Clique com o botão esquerdo no ícone da bandeja para abrir novamente.";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(2500);
        }
    }

    private void RestoreFromTray()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        BringToFront();
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

    private Panel CardPanel(int x, int y, int w, int h, Color? color = null, int radius = 12)
    {
        var p = new Panel { Bounds = new Rectangle(x, y, w, h), BackColor = color ?? Surface, Tag = radius };
        p.Paint += PaintRoundedPanel;
        return p;
    }

    private void LabelAt(string text, int x, int y) => Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        ForeColor = TextMuted,
        Location = new Point(x, y),
        Font = new Font("Segoe UI Variable Text", 7.8F, FontStyle.Bold)
    });

    private void StyleCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Surface2;
        combo.ForeColor = TextMain;
        combo.Font = new Font("Segoe UI Variable Text", 8.7F);
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 26;
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(selected ? Surface3 : Surface2);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var text = combo.Items[e.Index]?.ToString() ?? string.Empty;
            TextRenderer.DrawText(e.Graphics, text, combo.Font,
                new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height),
                selected ? Color.White : TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    private Button SmallActionButton(string text, Rectangle bounds)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            BackColor = Surface3,
            ForeColor = TextMain,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Variable Text", 7.8F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 55, 78);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 46, 67);
        RoundControl(button, 8);
        return button;
    }

    private void AddPresetButton(Control parent, string name, int x, int y, int w, int h)
    {
        var b = new Button
        {
            Text = name,
            Bounds = new Rectangle(x, y, w, h),
            BackColor = Surface2,
            ForeColor = TextMuted,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Variable Text", 8.2F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => { if (!_sharing) ApplyPreset(name); };
        parent.Controls.Add(b);
        RoundControl(b, 8);
        _presetButtons[name] = b;
    }

    private void PasteRoomCode()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var code = RelayClient.NormalizeRoomCode(Clipboard.GetText());
            if (code.Length > 6) code = code[..6];
            _roomCodeBox.Text = code;
            _roomCodeBox.SelectionStart = _roomCodeBox.TextLength;
        }
        catch { }
    }

    private void UpdateAudioButtonVisual()
    {
        _audioCheck.Text = _audioCheck.Checked ? "Ligado" : "Desligado";
        _audioCheck.BackColor = _audioCheck.Checked ? Color.FromArgb(20, 101, 133) : Surface3;
        _audioCheck.ForeColor = _audioCheck.Checked ? Color.FromArgb(178, 234, 255) : TextMuted;
        _audioCheck.FlatAppearance.MouseOverBackColor = _audioCheck.Checked ? Color.FromArgb(24, 116, 150) : Color.FromArgb(37, 55, 78);
    }

    private void UpdateTrayState()
    {
        if (!_tray.Visible) return;
        var viewers = _relay.ViewerCount;
        var status = !_sharing ? "Pronto" : viewers > 0 ? $"Ao vivo · {viewers} assistindo" : "Ligado · aguardando espectador";
        _trayStatusItem.Text = status;
        _trayToggleItem.Text = _sharing ? "Encerrar transmissão" : "Iniciar transmissão";
        var tooltip = $"AKTela Capture · {status}";
        _tray.Text = tooltip.Length <= 63 ? tooltip : "AKTela Capture";
    }

    private Button TitleButton(string text, int x)
    {
        var b = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, 13, 28, 28),
            BackColor = Bg,
            ForeColor = TextMuted,
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Variable Text", text == "×" ? 12F : 9F)
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Surface2;
        b.FlatAppearance.MouseDownBackColor = Surface3;
        RoundControl(b, 8);
        return b;
    }

    private static string ShortEncoder(string value) => value
        .Replace("NVIDIA NVENC (compatibilidade)", "NVENC compat.")
        .Replace("NVIDIA NVENC", "NVENC")
        .Replace("Media Foundation", "Media Foundation");

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _dot.BackColor = color;
        _dot.Invalidate();
        UpdateTrayState();
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        try { if (InvokeRequired) BeginInvoke(action); else action(); } catch { }
    }

    private static void PaintRoundedPanel(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel p) return;
        var radius = p.Tag is int value ? value : 12;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, p.Width - 1, p.Height - 1), radius);
        using var brush = new SolidBrush(p.BackColor);
        using var pen = new Pen(Stroke, 1F);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }

    private void PaintWindowBorder(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 18);
        using var pen = new Pen(Color.FromArgb(39, 56, 78), 1F);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void RoundControl(Control c, int radius)
    {
        using var p = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
        c.Region = new Region(p);
    }

    private void ApplyRoundedWindow()
    {
        using var p = RoundedRect(new Rectangle(0, 0, Width, Height), 18);
        Region = new Region(p);
    }

    private void ApplyDwmAttributes()
    {
        try
        {
            var dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var rounded = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
            }
        }
        catch { }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
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
