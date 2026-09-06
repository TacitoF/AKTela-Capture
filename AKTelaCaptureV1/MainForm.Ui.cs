using System.Drawing.Drawing2D;

namespace AKTelaCapture;

internal sealed partial class MainForm
{
    private readonly Dictionary<string, Button> _presetButtons = new();
    private readonly Button _refreshSources = new();
    private readonly Label _statusDot = new();
    private readonly Label _sourceSummary = new();
    private readonly ToolTip _tips = new();

    private void BuildUi()
    {
        SuspendLayout();
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(28, 20, 28, 16), BackColor = Bg
        };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);

        var header = Grid(2, 70, 30);
        header.Margin = new Padding(0, 0, 0, 22);
        var branding = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        branding.Controls.Add(new PictureBox
        {
            Image = Icon!.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(42, 42), Margin = new Padding(0, 3, 12, 0)
        });
        var title = Stack();
        title.Controls.Add(Copy("AKTela Capture", 21, TextColor, true));
        title.Controls.Add(Copy("Sua tela, na mesma conversa.", 10, Muted));
        branding.Controls.Add(title);
        header.Controls.Add(branding, 0, 0);
        var version = Copy("DESKTOP  /  2.3.4", 9, Muted, true);
        version.Anchor = AnchorStyles.Right;
        header.Controls.Add(version, 1, 0);
        shell.Controls.Add(header, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var body = Stack();
        body.Dock = DockStyle.Top;
        body.Padding = new Padding(0, 0, 4, 0);
        var setup = Stack();
        setup.Dock = DockStyle.Fill;
        setup.Margin = Padding.Empty;
        var overview = Stack();
        overview.Dock = DockStyle.Fill;
        overview.Margin = Padding.Empty;
        body.Controls.Add(setup);
        body.Controls.Add(overview);
        scroll.Controls.Add(body);
        shell.Controls.Add(scroll, 0, 1);

        var connection = Card("01", "Conecte sua Activity");
        connection.Controls.Add(Copy("Abra o AKTela no Discord e cole o código da sala.", 10, Muted));
        var codeRow = Grid(2, 76, 24);
        codeRow.Margin = new Padding(0, 14, 0, 0);
        var codeField = new Panel { Dock = DockStyle.Fill, BackColor = Surface2, Height = 48, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(12, 10, 12, 8) };
        _code.Dock = DockStyle.Fill;
        _code.BorderStyle = BorderStyle.None;
        _code.BackColor = Surface2;
        _code.ForeColor = TextColor;
        _code.Font = new Font("Consolas", 17, FontStyle.Bold);
        _code.CharacterCasing = CharacterCasing.Upper;
        _code.MaxLength = 6;
        _code.PlaceholderText = "ABC234";
        _code.AccessibleName = "Código da Activity, 6 caracteres";
        codeField.Controls.Add(_code);
        codeRow.Controls.Add(codeField, 0, 0);
        _paste.Text = "Colar";
        _paste.Dock = DockStyle.Fill;
        _paste.Margin = Padding.Empty;
        StyleButton(_paste, Surface2);
        _paste.Click += (_, _) =>
        {
            if (_sharing || _toggleBusy) return;
            try { _code.Text = RelayClient.Normalize(Clipboard.GetText()); }
            catch { SetStatus("Não foi possível colar", Yellow, "Digite o código da Activity no campo ao lado."); }
        };
        codeRow.Controls.Add(_paste, 1, 0);
        connection.Controls.Add(codeRow);
        _codeValidation.AutoSize = true;
        _codeValidation.Dock = DockStyle.Top;
        _codeValidation.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _codeValidation.Margin = new Padding(0, 7, 0, 0);
        connection.Controls.Add(_codeValidation);
        _code.TextChanged += (_, _) => UpdateCodeValidation();
        UpdateCodeValidation();
        setup.Controls.Add(connection);

        var capture = Card("02", "Escolha como compartilhar");
        var presets = Grid(3, 34, 33, 33);
        presets.Margin = new Padding(0, 10, 0, 16);
        var choices = new[] { ("Leve", "Uso diário"), ("Jogo", "Mais fluidez"), ("Filme", "Mais detalhe") };
        for (var i = 0; i < choices.Length; i++)
        {
            var (name, hint) = choices[i];
            var button = new Button
            {
                Text = name + "\n" + hint, Dock = DockStyle.Fill, Height = 64,
                Margin = new Padding(i == 0 ? 0 : 4, 0, i == 2 ? 0 : 4, 0),
                AccessibleName = $"Modo {name}: {hint}"
            };
            StyleButton(button, Surface2);
            button.Click += (_, _) => { if (!_sharing && !_toggleBusy) ApplyPreset(name); };
            _presetButtons.Add(name, button);
            presets.Controls.Add(button, i, 0);
        }
        capture.Controls.Add(presets);
        UpdatePresetButtons();
        var options = Grid(2, 50, 50);
        var qualityField = Field("Qualidade", _quality);
        qualityField.Margin = new Padding(0, 0, 8, 0);
        var typeField = Field("Compartilhar", _sourceType);
        typeField.Margin = new Padding(8, 0, 0, 0);
        options.Controls.Add(qualityField, 0, 0);
        options.Controls.Add(typeField, 1, 0);
        foreach (var q in QualityOption.All) _quality.Items.Add(q);
        _quality.SelectedIndex = 0;
        _sourceType.Items.AddRange(["Tela", "Janela"]);
        _sourceType.SelectedIndex = 0;
        capture.Controls.Add(options);
        var sourceHeader = Grid(2, 60, 40);
        sourceHeader.Margin = new Padding(0, 14, 0, 5);
        sourceHeader.Controls.Add(Copy("Fonte da captura", 10, Muted), 0, 0);
        _refreshSources.Text = "Atualizar lista";
        _refreshSources.AutoSize = true;
        _refreshSources.Anchor = AnchorStyles.Right;
        _refreshSources.Margin = Padding.Empty;
        StyleButton(_refreshSources, Surface);
        _refreshSources.ForeColor = Accent;
        _refreshSources.Click += (_, _) => LoadSources();
        sourceHeader.Controls.Add(_refreshSources, 1, 0);
        capture.Controls.Add(sourceHeader);
        StyleCombo(_source);
        _source.AccessibleName = "Janela ou monitor para compartilhar";
        capture.Controls.Add(_source);
        _source.SelectedIndexChanged += (_, _) => UpdateSourceSummary();
        _sourceSummary.AutoSize = true;
        _sourceSummary.Dock = DockStyle.Top;
        _sourceSummary.ForeColor = Muted;
        _sourceSummary.Font = new Font("Segoe UI", 9);
        _sourceSummary.Margin = new Padding(0, 9, 0, 0);
        capture.Controls.Add(_sourceSummary);
        setup.Controls.Add(capture);

        var state = Card("", "Sua transmissão");
        var stateLine = Grid(2, 8, 92);
        stateLine.Margin = new Padding(0, 14, 0, 6);
        _statusDot.Text = "●";
        _statusDot.AutoSize = true;
        _statusDot.ForeColor = Muted;
        _status.Text = "Pronto para começar";
        _status.AutoSize = true;
        _status.Dock = DockStyle.Top;
        _status.Font = new Font("Segoe UI", 15, FontStyle.Bold);
        stateLine.Controls.Add(_statusDot, 0, 0);
        stateLine.Controls.Add(_status, 1, 0);
        state.Controls.Add(stateLine);
        _detail.Text = "Conecte uma sala para iniciar.";
        _detail.ForeColor = Muted;
        _detail.AutoSize = false;
        _detail.Dock = DockStyle.Top;
        _detail.Height = 42;
        _detail.AutoEllipsis = true;
        _detail.Font = new Font("Segoe UI", 10);
        _detail.Margin = new Padding(0, 0, 0, 14);
        state.Controls.Add(_detail);
        var stats = Grid(2, 50, 50);
        stats.Controls.Add(Stat("RESOLUÇÃO", _outputValue), 0, 0);
        stats.Controls.Add(Stat("QUADROS / S", _fpsValue), 1, 0);
        stats.Controls.Add(Stat("ENCODER", _encoderValue), 0, 1);
        stats.Controls.Add(Stat("ASSISTINDO", _viewerValue), 1, 1);
        _viewerValue.Text = "0";
        state.Controls.Add(stats);
        overview.Controls.Add(state);

        var audio = Card("", "Áudio da transmissão");
        audio.Controls.Add(Copy("Compartilhe o som da fonte selecionada.", 10, Muted));
        _audioCheck.Text = "Áudio ligado";
        _audioCheck.Checked = true;
        _audioCheck.Appearance = Appearance.Button;
        _audioCheck.TextAlign = ContentAlignment.MiddleCenter;
        _audioCheck.Dock = DockStyle.Top;
        _audioCheck.Height = 40;
        _audioCheck.Margin = new Padding(0, 12, 0, 10);
        _audioCheck.FlatStyle = FlatStyle.Flat;
        _audioCheck.FlatAppearance.BorderSize = 0;
        _audioCheck.BackColor = Accent;
        _audioCheck.ForeColor = Bg;
        _audioCheck.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _audioCheck.Cursor = Cursors.Hand;
        _audioCheck.AccessibleName = "Ativar áudio da transmissão";
        _audioCheck.CheckedChanged += (_, _) =>
        {
            _audioCheck.Text = _audioCheck.Checked ? "Áudio ligado" : "Áudio desligado";
            _audioCheck.BackColor = _audioCheck.Checked ? Accent : Surface2;
            _audioCheck.ForeColor = _audioCheck.Checked ? Bg : TextColor;
        };
        audio.Controls.Add(_audioCheck);
        audio.Controls.Add(Copy("Evita eco do Discord quando possível.", 9, Muted));
        overview.Controls.Add(audio);

        var footer = Stack();
        footer.Dock = DockStyle.Fill;
        footer.Margin = new Padding(0, 16, 0, 0);
        var actions = Grid(2, 68, 32);
        _start.Text = "Iniciar transmissão";
        _start.Dock = DockStyle.Fill;
        _start.Height = 50;
        _start.Margin = new Padding(0, 0, 14, 0);
        StyleButton(_start, Accent);
        _start.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        actions.Controls.Add(_start, 0, 0);
        var diagnostics = new Button { Text = "Abrir diagnóstico", Dock = DockStyle.Fill, Margin = Padding.Empty };
        StyleButton(diagnostics, Surface2);
        diagnostics.Click += (_, _) => ShowDiagnostics();
        actions.Controls.Add(diagnostics, 1, 0);
        footer.Controls.Add(actions);
        var help = Copy("Ctrl + Shift + S  para iniciar ou encerrar   ·   Fechar a janela mantém o app na bandeja.", 9, Muted);
        help.Margin = new Padding(0, 12, 0, 0);
        footer.Controls.Add(help);
        shell.Controls.Add(footer, 0, 2);
        _tips.SetToolTip(_quality, "A qualidade se adapta à rede e aos espectadores.");
        _tips.SetToolTip(_refreshSources, "Recarregar monitores e janelas sem perder a fonte selecionada.");
        AcceptButton = _start;
        ResumeLayout(true);
    }

    private void UpdateSourceSummary()
    {
        var source = _source.SelectedItem as CaptureSource;
        _sourceSummary.Text = source is null
            ? "Nenhuma fonte disponível. Abra uma janela e atualize a lista."
            : $"{source.Width} × {source.Height}  ·  {(source.Kind == SourceKind.Display ? "Monitor inteiro" : "Janela selecionada")}";
        _tips.SetToolTip(_source, source?.Label ?? "Nenhuma fonte disponível");
    }

    private void UpdateCodeValidation()
    {
        var value = RelayClient.Normalize(_code.Text);
        var valid = RelayClient.IsValidCode(value);
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            _codeValidation.Text = "Você também pode digitar ou usar Ctrl+V.";
            _codeValidation.ForeColor = Muted;
            _code.ForeColor = TextColor;
        }
        else if (valid)
        {
            _codeValidation.Text = "✓ Código válido — pronto para conectar";
            _codeValidation.ForeColor = Accent;
            _code.ForeColor = Accent;
        }
        else
        {
            _codeValidation.Text = "Use exatamente os 6 caracteres mostrados na Activity.";
            _codeValidation.ForeColor = Yellow;
            _code.ForeColor = Yellow;
        }
        _code.AccessibleDescription = _codeValidation.Text;
    }

    private void UpdatePresetButtons()
    {
        foreach (var (name, button) in _presetButtons)
        {
            var selected = name == _preset;
            StyleButton(button, selected ? Color.FromArgb(37, 67, 60) : Surface2);
            button.ForeColor = selected ? Accent : TextColor;
            button.FlatAppearance.BorderColor = selected ? Accent : Surface2;
            button.FlatAppearance.BorderSize = selected ? 1 : 0;
            button.AccessibleDescription = selected ? "Modo selecionado" : "Selecionar modo";
        }
    }

    private static TableLayoutPanel Stack() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Dock = DockStyle.Top, Margin = Padding.Empty
    };

    private static TableLayoutPanel Grid(int columns, params float[] widths)
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns, Dock = DockStyle.Top, Margin = Padding.Empty
        };
        foreach (var width in widths) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        return grid;
    }

    private static Label Copy(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Top,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = color, Margin = new Padding(0, 0, 0, 4)
    };

    private static TableLayoutPanel Card(string number, string title)
    {
        var card = new CardLayout
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Dock = DockStyle.Top, BackColor = Surface,
            Padding = new Padding(20), Margin = new Padding(0, 0, 0, 16)
        };
        var heading = Copy(string.IsNullOrEmpty(number) ? title : $"{number}   {title}", 12, TextColor, true);
        heading.Margin = new Padding(0, 0, 0, 8);
        card.Controls.Add(heading);
        return card;
    }

    private static TableLayoutPanel Field(string label, ComboBox combo)
    {
        var field = Stack();
        field.Controls.Add(Copy(label, 10, Muted));
        StyleCombo(combo);
        combo.AccessibleName = label;
        field.Controls.Add(combo);
        return field;
    }

    private static TableLayoutPanel Stat(string title, Label value)
    {
        var stat = Stack();
        stat.Margin = new Padding(0, 6, 4, 10);
        stat.Controls.Add(Copy(title, 8, Muted, true));
        value.Text = "—";
        value.AutoSize = true;
        value.Dock = DockStyle.Top;
        value.Font = new Font("Segoe UI", 14, FontStyle.Bold);
        value.ForeColor = TextColor;
        value.Margin = Padding.Empty;
        stat.Controls.Add(value);
        return stat;
    }

    private static void StyleCombo(ComboBox combo)
    {
        combo.Dock = DockStyle.Top;
        combo.Margin = Padding.Empty;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Surface2;
        combo.ForeColor = TextColor;
        combo.Font = new Font("Segoe UI", 10);
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 30;
        combo.DropDownHeight = 260;
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var brush = new SolidBrush(selected ? Color.FromArgb(44, 74, 69) : Surface2);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? "", combo.Font,
                Rectangle.Inflate(e.Bounds, -8, 0), TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    private static void StyleButton(Button button, Color color)
    {
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.12f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(color, 0.12f);
        button.BackColor = color;
        button.ForeColor = color == Accent ? Bg : TextColor;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    private sealed class CardLayout : TableLayoutPanel
    {
        public CardLayout() => DoubleBuffered = true;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(44, 52, 65));
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
