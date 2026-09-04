using System.Runtime.InteropServices;

namespace AKTelaCapture;

internal sealed class MainForm : Form
{
    private readonly RelayClient _relay = new();
    private readonly VideoStreamer _video = new();
    private readonly AudioStreamer _audio = new();
    private readonly CursorTracker _cursor;

    private readonly ComboBox _quality = new();
    private readonly ComboBox _sourceType = new();
    private readonly ComboBox _source = new();
    private readonly TextBox _code = new();
    private readonly CheckBox _audioCheck = new();
    private readonly Button _start = new();
    private readonly Label _status = new();
    private readonly Label _detail = new();
    private readonly NotifyIcon _tray = new();

    private readonly SemaphoreSlim _mediaGate = new(1, 1);

    private bool _sharing;
    private bool _relayConnected;
    private bool _allowClose;

    private CaptureSource? _activeSource;
    private StreamConfig? _activeConfig;
    private AudioMode _activeAudio;
    private string _preset = "Leve";

    private static readonly Color Bg = Color.FromArgb(10, 14, 22);
    private static readonly Color Surface = Color.FromArgb(16, 23, 35);
    private static readonly Color Surface2 = Color.FromArgb(22, 33, 50);
    private static readonly Color TextColor = Color.FromArgb(239, 243, 250);
    private static readonly Color Muted = Color.FromArgb(145, 158, 179);
    private static readonly Color Accent = Color.FromArgb(50, 190, 236);
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
        ClientSize = new Size(430, 570);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        BuildTray();
        Wire();
        LoadSources();

        FormClosing += OnClosing;
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "AKTela Capture",
            Font = new Font("Segoe UI Variable Display", 15, FontStyle.Bold),
            ForeColor = TextColor,
            AutoSize = true,
            Location = new Point(62, 22)
        };
        Controls.Add(title);

        var sub = new Label
        {
            Text = "Compartilhamento leve e direto",
            Font = new Font("Segoe UI Variable Text", 8.5f),
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(63, 49)
        };
        Controls.Add(sub);

        var pic = new PictureBox
        {
            Image = Icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Bounds = new Rectangle(20, 20, 32, 32)
        };
        Controls.Add(pic);

        _status.Text = "Pronto";
        _status.Font = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Bold);
        _status.ForeColor = Muted;
        _status.AutoSize = true;
        _status.Location = new Point(20, 92);
        Controls.Add(_status);

        _detail.Text = "Cole o código exibido na Activity";
        _detail.Font = new Font("Segoe UI Variable Text", 8f);
        _detail.ForeColor = Muted;
        _detail.AutoSize = true;
        _detail.Location = new Point(20, 115);
        Controls.Add(_detail);

        Label("Código da Activity", 20, 153);

        _code.Bounds = new Rectangle(20, 174, 390, 38);
        StyleText(_code);

        var paste = new Button
        {
            Text = "Colar",
            Bounds = new Rectangle(328, 177, 78, 32)
        };
        StyleButton(paste, Surface2);
        paste.Click += (_, _) =>
        {
            try
            {
                _code.Text = RelayClient.Normalize(Clipboard.GetText());
            }
            catch
            {
            }
        };
        Controls.Add(paste);

        Label("Modo", 20, 231);

        var game = PresetButton("Jogo", 20, 253, "Jogo");
        var movie = PresetButton("Filme", 151, 253, "Filme");
        var light = PresetButton("Leve", 282, 253, "Leve");
        Controls.AddRange([game, movie, light]);

        Label("Qualidade", 20, 310);
        _quality.Bounds = new Rectangle(20, 331, 185, 36);
        StyleCombo(_quality);

        foreach (var q in QualityOption.All)
            _quality.Items.Add(q);

        _quality.SelectedItem = QualityOption.All[0];
        Controls.Add(_quality);

        Label("Fonte", 225, 310);
        _sourceType.Bounds = new Rectangle(225, 331, 185, 36);
        StyleCombo(_sourceType);
        _sourceType.Items.AddRange(["Tela", "Janela"]);
        _sourceType.SelectedIndex = 0;
        Controls.Add(_sourceType);

        Label("Janela ou tela", 20, 386);
        _source.Bounds = new Rectangle(20, 407, 390, 36);
        StyleCombo(_source);
        Controls.Add(_source);

        _audioCheck.Text = "Compartilhar áudio";
        _audioCheck.Checked = true;
        _audioCheck.ForeColor = TextColor;
        _audioCheck.BackColor = Bg;
        _audioCheck.Font = new Font("Segoe UI Variable Text", 9f);
        _audioCheck.AutoSize = true;
        _audioCheck.Location = new Point(20, 466);
        Controls.Add(_audioCheck);

        _start.Text = "Iniciar transmissão";
        _start.Bounds = new Rectangle(20, 508, 390, 44);
        StyleButton(_start, Accent);
        _start.Font = new Font("Segoe UI Variable Text", 10f, FontStyle.Bold);
        Controls.Add(_start);
    }

    private Button PresetButton(string text, int x, int y, string preset)
    {
        var b = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, 125, 38)
        };
        StyleButton(b, Surface2);
        b.Click += (_, _) => ApplyPreset(preset);
        return b;
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

        LoadSources();
    }

    private void Wire()
    {
        _sourceType.SelectedIndexChanged += (_, _) => LoadSources();
        _start.Click += async (_, _) => await Toggle();

        _relay.ConnectionChanged += connected => Ui(() =>
        {
            _relayConnected = connected;

            if (!connected && _sharing)
                _ = SyncMedia(0);

            RefreshStatus();
        });

        _relay.ViewerCountChanged += count => Ui(() =>
        {
            RefreshStatus();
            _ = SyncMedia(count);
        });

        _relay.LatencyChanged += _ => Ui(RefreshStatus);

        _relay.Error += msg => Ui(() =>
        {
            _detail.Text = msg;
            _detail.ForeColor = Red;
        });

        _video.PacketReady += p => _relay.QueuePacket(p);
        _audio.PacketReady += p => _relay.QueuePacket(p);

        _video.FpsChanged += fps => Ui(() =>
        {
            if (_sharing && fps > 0)
                _detail.Text = $"{fps:0} FPS · {_relay.LatencyMs} ms";
        });

        _video.EncoderChanged += name => Ui(() =>
        {
            if (_sharing)
                _detail.Text = name;
        });

        _video.StreamError += msg => Ui(() =>
            MessageBox.Show(this, msg, "Falha na captura", MessageBoxButtons.OK, MessageBoxIcon.Error));

        _audio.Error += msg => Ui(() =>
            MessageBox.Show(this, msg, "Falha no áudio", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    private void LoadSources()
    {
        if (_sharing)
            return;

        _source.Items.Clear();

        var type = _sourceType.SelectedItem?.ToString() ?? "Tela";
        var sources = type == "Janela"
            ? SourceEnumerator.Windows()
            : SourceEnumerator.Displays();

        foreach (var s in sources)
            _source.Items.Add(s);

        if (_source.Items.Count > 0)
            _source.SelectedIndex = 0;

        _start.Enabled = _source.Items.Count > 0;
    }

    private async Task Toggle()
    {
        if (_sharing)
        {
            await Stop();
            return;
        }

        if (_source.SelectedItem is not CaptureSource source ||
            _quality.SelectedItem is not QualityOption quality)
            return;

        var code = RelayClient.Normalize(_code.Text);

        var audioMode = _audioCheck.Checked
            ? (source.Kind == SourceKind.Window
                ? AudioMode.SourceOnly
                : AudioMode.SystemWithoutDiscord)
            : AudioMode.Off;

        var cursorPolicy = _preset == "Jogo" ? "Ocultar" : "Mostrar";

        var cfg = new StreamConfig(
            quality.Width,
            quality.Height,
            quality.Fps,
            quality.BitrateMbps,
            audioMode != AudioMode.Off,
            _preset,
            cursorPolicy);

        try
        {
            _start.Enabled = false;
            SetStatus("Conectando ao relay", Yellow, "Validando Cloudflare");

            await _relay.StartAsync(code, cfg);

            _sharing = true;
            _activeSource = source;
            _activeConfig = cfg;
            _activeAudio = audioMode;

            Lock(true);

            _start.Text = "Encerrar transmissão";
            _start.BackColor = Red;

            RefreshStatus();

            if (_relay.ViewerCount > 0)
                await SyncMedia(_relay.ViewerCount);
        }
        catch (Exception ex)
        {
            await _relay.StopAsync();
            MessageBox.Show(this, ex.Message, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _start.Enabled = true;
        }
    }

    private async Task SyncMedia(int viewers)
    {
        await _mediaGate.WaitAsync();

        try
        {
            if (!_sharing)
                return;

            if (viewers <= 0 || !_relayConnected)
            {
                _cursor.Stop();
                await _video.StopAsync();
                await _audio.StopAsync();
                return;
            }

            if (_video.IsRunning)
                return;

            var src = _activeSource;
            var cfg = _activeConfig;

            if (src is null || cfg is null)
                return;

            if (!File.Exists(FfmpegManager.PathToExe))
            {
                var progress = new Progress<int>(p =>
                    Ui(() => SetStatus($"Preparando encoder · {p}%", Yellow, "Primeira execução")));

                await FfmpegManager.EnsureAsync(progress);
            }

            await _video.StartAsync(src, cfg);

            if (_activeAudio != AudioMode.Off)
                await _audio.StartAsync(_activeAudio, src.ProcessId);

            _cursor.Start(src, () => cfg.CursorPolicy == "Mostrar");
        }
        catch (Exception ex)
        {
            Ui(() => MessageBox.Show(
                this,
                ex.Message,
                "AKTela Capture",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error));
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    private async Task Stop()
    {
        _sharing = false;

        _cursor.Stop();
        await _video.StopAsync();
        await _audio.StopAsync();
        await _relay.StopAsync();

        _activeSource = null;
        _activeConfig = null;

        Lock(false);

        _start.Text = "Iniciar transmissão";
        _start.BackColor = Accent;

        SetStatus("Pronto", Muted, "Cole o código exibido na Activity");
    }

    private void RefreshStatus()
    {
        if (!_sharing)
        {
            SetStatus("Pronto", Muted, "Cole o código exibido na Activity");
            return;
        }

        if (!_relayConnected)
        {
            SetStatus("Reconectando ao relay", Yellow, "A conexão será retomada automaticamente");
            return;
        }

        if (_relay.ViewerCount == 0)
        {
            SetStatus("Ligado · aguardando espectador", Green, $"Relay conectado · {_relay.LatencyMs} ms");
            return;
        }

        SetStatus(
            $"Ao vivo · {_relay.ViewerCount} assistindo",
            Green,
            $"{_activeConfig?.Height}p · {_activeConfig?.Fps} FPS · {_relay.LatencyMs} ms");
    }

    private void SetStatus(string text, Color color, string detail)
    {
        _status.Text = text;
        _status.ForeColor = color;
        _detail.Text = detail;
        _detail.ForeColor = Muted;
        UpdateTray();
    }

    private void Lock(bool locked)
    {
        _code.Enabled = !locked;
        _quality.Enabled = !locked;
        _sourceType.Enabled = !locked;
        _source.Enabled = !locked;
        _audioCheck.Enabled = !locked;
    }

    private void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "AKTela Capture";
        _tray.Visible = true;

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                Restore();
        };

        var menu = new ContextMenuStrip();

        menu.Items.Add("Abrir AKTela Capture", null, (_, _) => Restore());
        menu.Items.Add("Iniciar/encerrar", null, async (_, _) => await Toggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, async (_, _) =>
        {
            _allowClose = true;

            if (_sharing)
                await Stop();

            Close();
        });

        _tray.ContextMenuStrip = menu;
    }

    private void UpdateTray()
    {
        _tray.Text = _sharing
            ? (_relay.ViewerCount > 0
                ? $"AKTela · ao vivo · {_relay.ViewerCount} assistindo"
                : "AKTela · aguardando espectador")
            : "AKTela Capture · pronto";
    }

    private void Restore()
    {
        if (!Visible)
            Show();

        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnClosing(object? s, FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();

            _tray.ShowBalloonTip(
                1200,
                "AKTela continua ativo",
                "Clique com o botão esquerdo no ícone da bandeja para abrir.",
                ToolTipIcon.Info);
        }
        else
        {
            _tray.Visible = false;
            _mediaGate.Dispose();
        }
    }

    private void Label(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = Muted,
            Font = new Font("Segoe UI Variable Text", 8f, FontStyle.Bold)
        });
    }

    private static void StyleText(TextBox t)
    {
        t.BackColor = Surface2;
        t.ForeColor = TextColor;
        t.BorderStyle = BorderStyle.FixedSingle;
        t.Font = new Font("Segoe UI Variable Text", 10f);
    }

    private static void StyleCombo(ComboBox c)
    {
        c.DropDownStyle = ComboBoxStyle.DropDownList;
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Surface2;
        c.ForeColor = TextColor;
        c.Font = new Font("Segoe UI Variable Text", 9f);
    }

    private static void StyleButton(Button b, Color color)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = color;
        b.ForeColor = TextColor;
        b.Cursor = Cursors.Hand;
    }

    private void Ui(Action action)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }
}
