using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ScreenCapture.NET;

namespace AKTelaCapture;

internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(25, 26, 31), Card = Color.FromArgb(34, 36, 43), Card2 = Color.FromArgb(42, 44, 53);
    private static readonly Color TextMain = Color.FromArgb(245, 246, 250), TextMuted = Color.FromArgb(166, 171, 187);
    private static readonly Color Accent = Color.FromArgb(91, 105, 255), Green = Color.FromArgb(62, 207, 142), Yellow = Color.FromArgb(240, 178, 50), Red = Color.FromArgb(240, 92, 102);

    private readonly DX11ScreenCaptureService _displayService = new();
    private readonly RelayClient _relay = new();
    private readonly VideoStreamer _video = new();
    private readonly AudioStreamer _audio = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private readonly ComboBox _displayCombo = new(), _fpsCombo = new();
    private readonly TextBox _roomCodeBox = new();
    private readonly CheckBox _audioCheck = new();
    private readonly Button _toggle = new();
    private readonly Label _status = new();
    private Label _fpsValue = new(), _encoderValue = new(), _viewersValue = new();
    private readonly Panel _dot = new();
    private readonly NotifyIcon _tray = new();
    private bool _sharing, _connected, _allowClose;
    private DisplayOption? _activeDisplay;
    private StreamConfig? _activeConfig;

    public MainForm()
    {
        Text = "AKTela Capture"; ClientSize = new Size(382, 728); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None; BackColor = Bg; ForeColor = TextMain; Font = new Font("Segoe UI", 9F);
        MinimumSize = MaximumSize = Size; DoubleBuffered = true; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BuildUi(); LoadDisplays(); BuildTray(); HookEvents();
        Shown += (_, _) => ApplyRoundedWindow(); Resize += (_, _) => ApplyRoundedWindow(); FormClosing += OnFormClosing;
    }

    private void HookEvents()
    {
        _relay.ConnectionChanged += v => Ui(() => { _connected = v; RefreshStatus(); });
        _relay.ViewerCountChanged += v => { Ui(() => { _viewersValue.Text = v.ToString(); RefreshStatus(); }); _ = SyncMediaAsync(v); };
        _relay.RelayError += _ => Ui(() => { if (_sharing && !_connected) SetStatus("Reconectando ao servidor…", Yellow); });
        _video.PacketReady += p => _relay.TryQueuePacket(p);
        _video.FpsChanged += f => Ui(() => _fpsValue.Text = f <= 0 ? "—" : $"{f:0} FPS");
        _video.EncoderChanged += e => Ui(() => _encoderValue.Text = e.Replace("GPU • ", ""));
        _video.StreamError += e => Ui(() => { SetStatus("Falha no encoder de vídeo", Red); MessageBox.Show(this, e, "AKTela Capture", MessageBoxButtons.OK, MessageBoxIcon.Error); });
        _audio.PacketReady += p => _relay.TryQueuePacket(p);
        _audio.AudioError += _ => Ui(() => { _audioCheck.Checked = false; _audioCheck.Text = "Áudio do sistema indisponível"; });
    }

    private void BuildUi()
    {
        var drag = new Panel { Bounds = new Rectangle(0,0,382,54), BackColor = Bg }; drag.MouseDown += DragWindow; Controls.Add(drag);
        var logo = new Panel { Bounds = new Rectangle(22,15,28,28), BackColor = Accent }; logo.Paint += (_,e)=> TextRenderer.DrawText(e.Graphics,"AK",new Font("Segoe UI",8.5F,FontStyle.Bold),logo.ClientRectangle,Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter); drag.Controls.Add(logo); RoundControl(logo,8);
        var appTitle = new Label { Text="AKTela Capture", AutoSize=true, Font=new Font("Segoe UI Semibold",11F,FontStyle.Bold), ForeColor=TextMain, Location=new Point(59,18) }; appTitle.MouseDown += DragWindow; drag.Controls.Add(appTitle);
        var min=TitleButton("—",309); min.Click += (_,_)=>HideToTray(); drag.Controls.Add(min); var close=TitleButton("×",344); close.Click += (_,_)=>HideToTray(); drag.Controls.Add(close);
        Controls.Add(new Label { Text="1080p com baixo impacto", Bounds=new Rectangle(28,70,326,34), TextAlign=ContentAlignment.MiddleCenter, Font=new Font("Segoe UI Semibold",17F,FontStyle.Bold), ForeColor=TextMain });
        Controls.Add(new Label { Text="Encoder por hardware para jogos, vídeos e compartilhamento com baixa latência.", Bounds=new Rectangle(37,108,308,43), TextAlign=ContentAlignment.TopCenter, ForeColor=TextMuted });
        var statusCard = CardPanel(31,157,320,54); Controls.Add(statusCard); _dot.Bounds=new Rectangle(18,22,10,10); _dot.BackColor=TextMuted; _dot.Paint += (_,e)=>{using var b=new SolidBrush(_dot.BackColor);e.Graphics.FillEllipse(b,0,0,10,10);}; statusCard.Controls.Add(_dot); _status.Text="Pronto para compartilhar"; _status.AutoSize=true; _status.Font=new Font("Segoe UI Semibold",9.5F,FontStyle.Bold); _status.ForeColor=TextMain; _status.Location=new Point(38,18); statusCard.Controls.Add(_status);

        LabelAt("Código da Activity",33,228); _roomCodeBox.Bounds=new Rectangle(31,251,320,32); _roomCodeBox.BackColor=Card2; _roomCodeBox.ForeColor=TextMain; _roomCodeBox.BorderStyle=BorderStyle.FixedSingle; _roomCodeBox.CharacterCasing=CharacterCasing.Upper; _roomCodeBox.MaxLength=6; _roomCodeBox.TextAlign=HorizontalAlignment.Center; _roomCodeBox.Font=new Font("Consolas",13F,FontStyle.Bold); _roomCodeBox.Text=RelayClient.NormalizeRoomCode(_settings.RoomCode); Controls.Add(_roomCodeBox);
        LabelAt("Tela",33,302); _displayCombo.Bounds=new Rectangle(31,325,320,32); StyleCombo(_displayCombo); Controls.Add(_displayCombo);
        LabelAt("Qualidade",33,374); _fpsCombo.Bounds=new Rectangle(31,397,320,32); StyleCombo(_fpsCombo); _fpsCombo.Items.AddRange(["30 FPS  •  mais leve", "60 FPS  •  mais fluido"]); _fpsCombo.SelectedIndex=_settings.Fps>=60?1:0; Controls.Add(_fpsCombo);
        _audioCheck.Text="Compartilhar áudio do sistema"; _audioCheck.Checked=_settings.IncludeSystemAudio; _audioCheck.ForeColor=TextMain; _audioCheck.BackColor=Bg; _audioCheck.Bounds=new Rectangle(33,446,300,26); Controls.Add(_audioCheck);

        var live=CardPanel(31,488,320,76); Controls.Add(live); AddMetric(live,"Saída","1080p",14,10,out _); AddMetric(live,"Captura","—",108,10,out _fpsValue); AddMetric(live,"Encoder","—",196,10,out _encoderValue); AddMetric(live,"Assistindo","0",270,10,out _viewersValue);
        _toggle.Bounds=new Rectangle(31,588,320,62); _toggle.Text="Ligar compartilhamento"; _toggle.BackColor=Accent; _toggle.ForeColor=Color.White; _toggle.FlatStyle=FlatStyle.Flat; _toggle.FlatAppearance.BorderSize=0; _toggle.Font=new Font("Segoe UI Semibold",11F,FontStyle.Bold); _toggle.Cursor=Cursors.Hand; _toggle.Click += async(_,_)=>await ToggleAsync(); Controls.Add(_toggle); RoundControl(_toggle,14);
        Controls.Add(new Label { Text="Sem espectadores, a captura e o encoder ficam parados para economizar recursos.", Bounds=new Rectangle(33,665,316,42), TextAlign=ContentAlignment.MiddleCenter, ForeColor=TextMuted, Font=new Font("Segoe UI",8.3F) });
    }

    private void LoadDisplays()
    {
        try
        {
            _displayCombo.Items.Clear(); var n=1; var ff=0;
            foreach(var card in _displayService.GetGraphicsCards()) foreach(var display in _displayService.GetDisplays(card)) _displayCombo.Items.Add(new DisplayOption(display,n++,ff++));
            if(_displayCombo.Items.Count>0) _displayCombo.SelectedIndex=0; else { SetStatus("Nenhuma tela encontrada",Red); _toggle.Enabled=false; }
        }
        catch(Exception ex){ SetStatus("Falha ao listar as telas",Red); _toggle.Enabled=false; MessageBox.Show(this,ex.Message,"AKTela Capture"); }
    }

    private async Task ToggleAsync()
    {
        if(_sharing){await StopSharingAsync();return;}
        if(_displayCombo.SelectedItem is not DisplayOption) return;
        var room=RelayClient.NormalizeRoomCode(_roomCodeBox.Text);
        if(room.Length!=6){MessageBox.Show(this,"Cole o código de 6 caracteres mostrado na AKTela dentro do Discord.","Código da Activity");return;}
        var fps=_fpsCombo.SelectedIndex==1?60:30; var config=new StreamConfig(fps,fps==60?12:8,_audioCheck.Checked);
        _activeDisplay=(DisplayOption)_displayCombo.SelectedItem; _activeConfig=config;
        _settings.RoomCode=room;_settings.Fps=fps;_settings.IncludeSystemAudio=_audioCheck.Checked;_settings.Save();
        _toggle.Enabled=false; SetStatus("Conectando ao servidor…",Yellow);
        try
        {
            await _relay.StartAsync(room,config); _sharing=true; LockInputs(true); _toggle.Text="Desligar compartilhamento"; _toggle.BackColor=Red; RefreshStatus();
            if(_relay.ViewerCount>0) await SyncMediaAsync(_relay.ViewerCount);
        }
        catch(Exception ex){await _relay.StopAsync();MessageBox.Show(this,ex.Message,"AKTela Capture");SetStatus("Não foi possível conectar",Red);}
        finally{_toggle.Enabled=true;}
    }

    private async Task SyncMediaAsync(int viewers)
    {
        await _mediaGate.WaitAsync();
        try
        {
            if(!_sharing) return;
            if(viewers<=0){ await _video.StopAsync(); await _audio.StopAsync(); Ui(()=>{_fpsValue.Text="—";_encoderValue.Text="—";RefreshStatus();}); return; }
            if(_video.IsRunning) return;
            var display=_activeDisplay; var cfg=_activeConfig; if(display is null||cfg is null) return;
            var fps=cfg.Fps;
            if(!File.Exists(FfmpegManager.FfmpegPath))
            {
                Ui(()=>SetStatus("Preparando encoder • 0%",Yellow));
                var progress=new Progress<int>(p=>Ui(()=>SetStatus($"Preparando encoder • {p}%",Yellow)));
                await FfmpegManager.EnsureAsync(progress);
            }
            await _video.StartAsync(display.FfmpegOutputIndex, display.Display.Width, display.Display.Height, fps);
            if(cfg.AudioEnabled) await _audio.StartAsync();
            Ui(RefreshStatus);
        }
        catch(Exception ex){Ui(()=>{SetStatus("Falha ao iniciar mídia",Red);MessageBox.Show(this,ex.Message,"AKTela Capture");});}
        finally{_mediaGate.Release();}
    }

    private async Task StopSharingAsync()
    {
        _toggle.Enabled=false; _sharing=false; await _video.StopAsync(); await _audio.StopAsync(); await _relay.StopAsync(); _connected=false; _activeDisplay=null; _activeConfig=null; LockInputs(false); _toggle.Text="Ligar compartilhamento"; _toggle.BackColor=Accent; _fpsValue.Text="—";_encoderValue.Text="—";_viewersValue.Text="0";SetStatus("Pronto para compartilhar",TextMuted);_toggle.Enabled=true;
    }

    private void RefreshStatus()
    {
        if(!_sharing){SetStatus("Pronto para compartilhar",TextMuted);return;}
        if(!_connected){SetStatus("Conectando ao servidor…",Yellow);return;}
        if(_relay.ViewerCount==0){SetStatus("Ligado • aguardando espectador",Green);return;}
        SetStatus($"Ao vivo • {_relay.ViewerCount} assistindo",Green);
    }
    private void LockInputs(bool locked){_displayCombo.Enabled=!locked;_roomCodeBox.Enabled=!locked;_fpsCombo.Enabled=!locked;_audioCheck.Enabled=!locked;}

    private void BuildTray()
    {
        _tray.Icon=Icon;_tray.Text="AKTela Capture";_tray.Visible=true;_tray.DoubleClick+=(_,_)=>RestoreFromTray();
        var menu=new ContextMenuStrip(); menu.Items.Add("Abrir",null,(_,_)=>RestoreFromTray()); menu.Items.Add("Sair",null,async(_,_)=>{_allowClose=true;await StopSharingAsync();Close();});_tray.ContextMenuStrip=menu;
    }
    private void HideToTray(){Hide();_tray.ShowBalloonTip(900,"AKTela Capture","O Capture continua em segundo plano.",ToolTipIcon.Info);} private void RestoreFromTray(){Show();WindowState=FormWindowState.Normal;Activate();}
    private void OnFormClosing(object? s,FormClosingEventArgs e){if(!_allowClose){e.Cancel=true;HideToTray();}else{_tray.Visible=false;_displayService.Dispose();_mediaGate.Dispose();}}

    private Panel CardPanel(int x,int y,int w,int h){var p=new Panel{Bounds=new Rectangle(x,y,w,h),BackColor=Card};p.Paint+=PaintRoundedPanel;return p;}
    private void LabelAt(string text,int x,int y)=>Controls.Add(new Label{Text=text,AutoSize=true,ForeColor=TextMuted,Location=new Point(x,y)});
    private void StyleCombo(ComboBox c){c.DropDownStyle=ComboBoxStyle.DropDownList;c.FlatStyle=FlatStyle.Flat;c.BackColor=Card2;c.ForeColor=TextMain;c.Font=new Font("Segoe UI",10F);}
    private void AddMetric(Panel p,string title,string value,int x,int y,out Label label){p.Controls.Add(new Label{Text=title,AutoSize=true,ForeColor=TextMuted,Location=new Point(x,y),Font=new Font("Segoe UI",7.5F)});label=new Label{Text=value,AutoSize=true,ForeColor=TextMain,Location=new Point(x,y+26),Font=new Font("Segoe UI Semibold",8.5F,FontStyle.Bold)};p.Controls.Add(label);}
    private Button TitleButton(string t,int x){var b=new Button{Text=t,Bounds=new Rectangle(x,12,30,30),BackColor=Bg,ForeColor=TextMuted,FlatStyle=FlatStyle.Flat,TabStop=false,Cursor=Cursors.Hand,Font=new Font("Segoe UI",t=="×"?13F:10F)};b.FlatAppearance.BorderSize=0;return b;}
    private void SetStatus(string text,Color color){_status.Text=text;_dot.BackColor=color;_dot.Invalidate();}
    private void Ui(Action a){if(IsDisposed)return;try{if(InvokeRequired)BeginInvoke(a);else a();}catch{}}
    private static void PaintRoundedPanel(object? sender,PaintEventArgs e){if(sender is not Panel p)return;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var path=RoundedRect(p.ClientRectangle,12);using var brush=new SolidBrush(p.BackColor);e.Graphics.FillPath(brush,path);}
    private static GraphicsPath RoundedRect(Rectangle r,int rad){var d=rad*2;var p=new GraphicsPath();p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    private static void RoundControl(Control c,int rad){using var p=RoundedRect(new Rectangle(0,0,c.Width,c.Height),rad);c.Region=new Region(p);}
    private void ApplyRoundedWindow(){using var p=RoundedRect(new Rectangle(0,0,Width,Height),18);Region=new Region(p);}
    [DllImport("user32.dll")] private static extern bool ReleaseCapture(); [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd,int msg,int wp,int lp);
    private void DragWindow(object? s,MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;ReleaseCapture();SendMessage(Handle,0xA1,0x2,0);}
}
