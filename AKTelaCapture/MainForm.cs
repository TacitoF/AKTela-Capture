using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AKTelaCapture;

internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(25, 26, 31);
    private static readonly Color Card = Color.FromArgb(34, 36, 43);
    private static readonly Color Card2 = Color.FromArgb(42, 44, 53);
    private static readonly Color TextMain = Color.FromArgb(245, 246, 250);
    private static readonly Color TextMuted = Color.FromArgb(166, 171, 187);
    private static readonly Color Accent = Color.FromArgb(91, 105, 255);
    private static readonly Color AccentHover = Color.FromArgb(105, 118, 255);
    private static readonly Color Green = Color.FromArgb(62, 207, 142);
    private static readonly Color Yellow = Color.FromArgb(240, 178, 50);
    private static readonly Color Red = Color.FromArgb(240, 92, 102);

    private readonly CaptureController _capture = new();
    private readonly RelayClient _relay = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ComboBox _displayCombo = new();
    private readonly TextBox _roomCodeBox = new();
    private readonly Button _toggleButton = new();
    private readonly Label _statusLabel = new();
    private readonly Panel _statusDot = new();
    private readonly Label _fpsLabel = new();
    private readonly Label _resolutionLabel = new();
    private readonly Label _viewersLabel = new();
    private readonly NotifyIcon _trayIcon = new();

    private bool _isCapturing;
    private bool _relayConnected;
    private bool _allowClose;

    public MainForm()
    {
        Text = "AKTela Capture";
        ClientSize = new Size(382, 638);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        MaximizeBox = false;
        MinimumSize = MaximumSize = Size;
        DoubleBuffered = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        BuildUi();
        LoadDisplays();
        BuildTrayIcon();

        _capture.ShouldEncodeFrame = () => _relay.ViewerCount > 0;
        _capture.FpsChanged += fps => BeginInvokeSafe(() => _fpsLabel.Text = fps <= 0 ? "—" : $"{fps:0} FPS");
        _capture.FrameReady += frame => _relay.TryQueueFrame(frame);
        _capture.CaptureError += message => BeginInvokeSafe(() => OnCaptureError(message));

        _relay.ConnectionChanged += connected => BeginInvokeSafe(() =>
        {
            _relayConnected = connected;
            RefreshLiveStatus();
        });
        _relay.ViewerCountChanged += count => BeginInvokeSafe(() =>
        {
            _viewersLabel.Text = count.ToString();
            RefreshLiveStatus();
        });
        _relay.RelayError += _ => BeginInvokeSafe(() =>
        {
            if (_isCapturing && !_relayConnected)
                SetStatus("Reconectando ao servidor…", Yellow);
        });

        Shown += (_, _) => ApplyRoundedWindow();
        Resize += (_, _) => ApplyRoundedWindow();
        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        var dragBar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, 54),
            BackColor = Bg
        };
        dragBar.MouseDown += DragWindow;
        Controls.Add(dragBar);

        var logo = new Panel
        {
            Location = new Point(22, 15),
            Size = new Size(28, 28),
            BackColor = Accent
        };
        logo.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, "AK", font, logo.ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        logo.Resize += (_, _) => RoundControl(logo, 8);
        dragBar.Controls.Add(logo);
        RoundControl(logo, 8);

        var title = new Label
        {
            AutoSize = true,
            Text = "AKTela Capture",
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            ForeColor = TextMain,
            Location = new Point(59, 18)
        };
        title.MouseDown += DragWindow;
        dragBar.Controls.Add(title);

        var minimize = MakeTitleButton("—", 309);
        minimize.Click += (_, _) => HideToTray();
        dragBar.Controls.Add(minimize);

        var close = MakeTitleButton("×", 344);
        close.Click += (_, _) => HideToTray();
        dragBar.Controls.Add(close);

        var heading = new Label
        {
            Text = "Compartilhe sem complicação",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold),
            ForeColor = TextMain,
            Location = new Point(28, 70),
            Size = new Size(326, 34)
        };
        Controls.Add(heading);

        var subtitle = new Label
        {
            Text = "Cole o código mostrado na Activity e deixe o Capture trabalhar em segundo plano.",
            AutoSize = false,
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", 9F),
            ForeColor = TextMuted,
            Location = new Point(37, 108),
            Size = new Size(308, 43)
        };
        Controls.Add(subtitle);

        var statusCard = new Panel
        {
            Location = new Point(31, 157),
            Size = new Size(320, 54),
            BackColor = Card
        };
        statusCard.Paint += PaintRoundedPanel;
        Controls.Add(statusCard);

        _statusDot.Size = new Size(10, 10);
        _statusDot.Location = new Point(18, 22);
        _statusDot.BackColor = TextMuted;
        _statusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_statusDot.BackColor);
            e.Graphics.FillEllipse(brush, 0, 0, 10, 10);
        };
        statusCard.Controls.Add(_statusDot);

        _statusLabel.Text = "Pronto para compartilhar";
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = TextMain;
        _statusLabel.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
        _statusLabel.Location = new Point(38, 18);
        statusCard.Controls.Add(_statusLabel);

        Controls.Add(new Label
        {
            Text = "Código da Activity",
            AutoSize = true,
            ForeColor = TextMuted,
            Location = new Point(33, 228)
        });

        _roomCodeBox.Location = new Point(31, 251);
        _roomCodeBox.Size = new Size(320, 32);
        _roomCodeBox.BorderStyle = BorderStyle.FixedSingle;
        _roomCodeBox.BackColor = Card2;
        _roomCodeBox.ForeColor = TextMain;
        _roomCodeBox.CharacterCasing = CharacterCasing.Upper;
        _roomCodeBox.MaxLength = 6;
        _roomCodeBox.TextAlign = HorizontalAlignment.Center;
        _roomCodeBox.Font = new Font("Consolas", 13F, FontStyle.Bold);
        _roomCodeBox.Text = RelayClient.NormalizeRoomCode(_settings.RoomCode);
        _roomCodeBox.TextChanged += (_, _) =>
        {
            var caret = _roomCodeBox.SelectionStart;
            var normalized = RelayClient.NormalizeRoomCode(_roomCodeBox.Text);
            if (_roomCodeBox.Text != normalized)
            {
                _roomCodeBox.Text = normalized;
                _roomCodeBox.SelectionStart = Math.Min(caret, normalized.Length);
            }
        };
        Controls.Add(_roomCodeBox);

        Controls.Add(new Label
        {
            Text = "Tela",
            AutoSize = true,
            ForeColor = TextMuted,
            Location = new Point(33, 302)
        });

        _displayCombo.Location = new Point(31, 325);
        _displayCombo.Size = new Size(320, 34);
        _displayCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _displayCombo.FlatStyle = FlatStyle.Flat;
        _displayCombo.BackColor = Card2;
        _displayCombo.ForeColor = TextMain;
        _displayCombo.Font = new Font("Segoe UI", 10F);
        _displayCombo.SelectedIndexChanged += (_, _) => UpdateResolutionLabel();
        Controls.Add(_displayCombo);

        Controls.Add(new Label
        {
            Text = "Modo leve",
            AutoSize = true,
            ForeColor = TextMuted,
            Location = new Point(33, 378)
        });

        var perfCard = new Panel
        {
            Location = new Point(31, 401),
            Size = new Size(320, 64),
            BackColor = Card
        };
        perfCard.Paint += PaintRoundedPanel;
        Controls.Add(perfCard);

        perfCard.Controls.Add(new Label { Text = "Captura", ForeColor = TextMuted, AutoSize = true, Location = new Point(16, 9) });
        _fpsLabel.Text = "—";
        _fpsLabel.ForeColor = TextMain;
        _fpsLabel.AutoSize = true;
        _fpsLabel.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
        _fpsLabel.Location = new Point(16, 31);
        perfCard.Controls.Add(_fpsLabel);

        perfCard.Controls.Add(new Label { Text = "Fonte", ForeColor = TextMuted, AutoSize = true, Location = new Point(120, 9) });
        _resolutionLabel.Text = "—";
        _resolutionLabel.ForeColor = TextMain;
        _resolutionLabel.AutoSize = true;
        _resolutionLabel.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
        _resolutionLabel.Location = new Point(120, 31);
        perfCard.Controls.Add(_resolutionLabel);

        perfCard.Controls.Add(new Label { Text = "Assistindo", ForeColor = TextMuted, AutoSize = true, Location = new Point(250, 9) });
        _viewersLabel.Text = "0";
        _viewersLabel.ForeColor = TextMain;
        _viewersLabel.AutoSize = true;
        _viewersLabel.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
        _viewersLabel.Location = new Point(250, 31);
        perfCard.Controls.Add(_viewersLabel);

        _toggleButton.Location = new Point(31, 489);
        _toggleButton.Size = new Size(320, 62);
        _toggleButton.FlatStyle = FlatStyle.Flat;
        _toggleButton.FlatAppearance.BorderSize = 0;
        _toggleButton.BackColor = Accent;
        _toggleButton.ForeColor = Color.White;
        _toggleButton.Text = "Ligar compartilhamento";
        _toggleButton.Cursor = Cursors.Hand;
        _toggleButton.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold);
        _toggleButton.MouseEnter += (_, _) => { if (!_isCapturing) _toggleButton.BackColor = AccentHover; };
        _toggleButton.MouseLeave += (_, _) => { if (!_isCapturing) _toggleButton.BackColor = Accent; };
        _toggleButton.Click += async (_, _) => await ToggleCaptureAsync();
        _toggleButton.Resize += (_, _) => RoundControl(_toggleButton, 14);
        Controls.Add(_toggleButton);
        RoundControl(_toggleButton, 14);

        Controls.Add(new Label
        {
            Text = "Depois de ligar, você pode minimizar. O app continua na bandeja do Windows.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMuted,
            Location = new Point(33, 566),
            Size = new Size(316, 42),
            Font = new Font("Segoe UI", 8.3F)
        });
    }

    private Button MakeTitleButton(string text, int x)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, 12),
            Size = new Size(30, 30),
            BackColor = Bg,
            ForeColor = TextMuted,
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", text == "×" ? 13F : 10F)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void LoadDisplays()
    {
        try
        {
            var displays = _capture.GetDisplays();
            _displayCombo.Items.Clear();
            foreach (var display in displays)
                _displayCombo.Items.Add(display);

            if (_displayCombo.Items.Count > 0)
            {
                _displayCombo.SelectedIndex = 0;
                SetStatus("Pronto para compartilhar", TextMuted);
            }
            else
            {
                SetStatus("Nenhuma tela encontrada", Red);
                _toggleButton.Enabled = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Falha ao iniciar a captura", Red);
            _toggleButton.Enabled = false;
            MessageBox.Show(this, ex.Message, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ToggleCaptureAsync()
    {
        if (_isCapturing)
        {
            await StopCaptureAsync();
            return;
        }

        if (_displayCombo.SelectedItem is not DisplayOption selected) return;

        var roomCode = RelayClient.NormalizeRoomCode(_roomCodeBox.Text);
        if (roomCode.Length != 6 || roomCode.Any(c => !(c is >= 'A' and <= 'Z' || c is >= '2' and <= '9')))
        {
            MessageBox.Show(this,
                "Copie o código de 6 caracteres mostrado na parte inferior da AKTela dentro do Discord.",
                "Código da Activity",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            _roomCodeBox.Focus();
            return;
        }

        _settings.RoomCode = roomCode;
        _settings.Save();
        _toggleButton.Enabled = false;
        SetStatus("Conectando ao servidor…", Yellow);

        try
        {
            await _relay.StartAsync(roomCode);
            await _capture.StartAsync(selected.Display, 15);

            _isCapturing = true;
            _displayCombo.Enabled = false;
            _roomCodeBox.Enabled = false;
            _toggleButton.Text = "Desligar compartilhamento";
            _toggleButton.BackColor = Red;
            RefreshLiveStatus();
        }
        catch (Exception ex)
        {
            await _relay.StopAsync();
            OnCaptureError(ex.Message);
        }
        finally
        {
            _toggleButton.Enabled = true;
        }
    }

    private async Task StopCaptureAsync()
    {
        _toggleButton.Enabled = false;
        await _capture.StopAsync();
        await _relay.StopAsync();

        _isCapturing = false;
        _relayConnected = false;
        _displayCombo.Enabled = true;
        _roomCodeBox.Enabled = true;
        _toggleButton.Text = "Ligar compartilhamento";
        _toggleButton.BackColor = Accent;
        _fpsLabel.Text = "—";
        _viewersLabel.Text = "0";
        SetStatus("Pronto para compartilhar", TextMuted);
        _toggleButton.Enabled = true;
    }

    private void RefreshLiveStatus()
    {
        if (!_isCapturing) return;

        if (!_relayConnected)
        {
            SetStatus("Reconectando ao servidor…", Yellow);
            return;
        }

        var viewers = _relay.ViewerCount;
        SetStatus(viewers > 0
            ? $"Transmitindo • {viewers} assistindo"
            : "Ligado • aguardando espectadores", Green);
    }

    private void OnCaptureError(string message)
    {
        _isCapturing = false;
        _displayCombo.Enabled = true;
        _roomCodeBox.Enabled = true;
        _toggleButton.Enabled = true;
        _toggleButton.Text = "Tentar novamente";
        _toggleButton.BackColor = Accent;
        SetStatus("Erro na captura", Red);
        MessageBox.Show(this, message, "Não foi possível compartilhar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void UpdateResolutionLabel()
    {
        if (_displayCombo.SelectedItem is DisplayOption selected)
            _resolutionLabel.Text = $"{selected.Display.Width}×{selected.Display.Height}";
    }

    private void SetStatus(string text, Color dotColor)
    {
        _statusLabel.Text = text;
        _statusDot.BackColor = dotColor;
        _statusDot.Invalidate();
    }

    private void BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir AKTela Capture", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Desligar compartilhamento", null, async (_, _) =>
        {
            if (_isCapturing) await StopCaptureAsync();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, async (_, _) =>
        {
            _allowClose = true;
            if (_isCapturing) await StopCaptureAsync();
            Close();
        });

        _trayIcon.Icon = Icon;
        _trayIcon.Text = "AKTela Capture";
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _trayIcon.Visible = true;
        _trayIcon.BalloonTipTitle = "AKTela Capture";
        _trayIcon.BalloonTipText = _isCapturing
            ? "O compartilhamento continua ligado."
            : "O Capture continua disponível em segundo plano.";
        _trayIcon.ShowBalloonTip(1200);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_isCapturing)
        {
            await _capture.StopAsync();
            await _relay.StopAsync();
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _capture.Dispose();
        await _relay.DisposeAsync();
    }

    private void PaintRoundedPanel(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel panel) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(panel.ClientRectangle, 14);
        using var brush = new SolidBrush(panel.BackColor);
        e.Graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var rect = new Rectangle(bounds.Location, new Size(diameter, diameter));
        var path = new GraphicsPath();
        path.AddArc(rect, 180, 90);
        rect.X = bounds.Right - diameter;
        path.AddArc(rect, 270, 90);
        rect.Y = bounds.Bottom - diameter;
        path.AddArc(rect, 0, 90);
        rect.X = bounds.Left;
        path.AddArc(rect, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void RoundControl(Control control, int radius)
    {
        using var path = RoundedRect(control.ClientRectangle, radius);
        control.Region = new Region(path);
    }

    private void ApplyRoundedWindow() => RoundControl(this, 18);

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
