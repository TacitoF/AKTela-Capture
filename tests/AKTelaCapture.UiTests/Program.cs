using System.Reflection;
using System.Drawing.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var type = Assembly.Load("AKTela Capture").GetType("AKTelaCapture.MainForm", true)!;
        using var form = (Form)Activator.CreateInstance(type)!;
        form.Show();
        Application.DoEvents();
        Check(form.Height > form.Width, "A janela principal deve preservar a identidade vertical");
        Check(form.FormBorderStyle == FormBorderStyle.Sizable,
            "A janela principal deve permitir redimensionamento pelo usuário");
        Check(form.MaximizeBox, "A janela deve oferecer maximização em telas menores");
        Check(form.SizeGripStyle == SizeGripStyle.Show, "A alça de redimensionamento deve ficar visível");
        Check(form.Bounds.Width <= Screen.FromControl(form).WorkingArea.Width &&
              form.Bounds.Height <= Screen.FromControl(form).WorkingArea.Height,
            "A janela inicial deve caber na área útil do monitor");
        var fitted = (Rectangle)InvokeStatic(type, "FitBoundsToWorkingArea",
            new Rectangle(100, 50, 1280, 720), new Size(840, 1300))!;
        Check(fitted.Left >= 100 && fitted.Top >= 50 && fitted.Right <= 1380 && fitted.Bottom <= 770,
            "O cálculo responsivo deve manter toda a janela dentro do monitor");
        var processHelper = type.Assembly.GetType("AKTelaCapture.ProcessTreeHelper", true)!;
        var selectedRoot = (int?)InvokeStatic(processHelper, "SelectRootProcessId",
            new[] { 1, 2, 3, 10, 11 },
            new Dictionary<int, int> { [2] = 1, [3] = 1, [11] = 10 },
            new[] { 11 });
        Check(selectedRoot == 10, "A árvore do Discord com áudio ativo deve ter prioridade");
        Directory.CreateDirectory("ui-captures");
        Capture(form, "desktop");
        form.Size = new Size(460, 620);
        Application.DoEvents();
        Capture(form, "compact");
        var start = Field<Button>(form, "_start");
        var code = Field<TextBox>(form, "_code");
        Check(!string.IsNullOrWhiteSpace(Field<TextBox>(form, "_displayName").Text),
            "Nome do transmissor deve vir preenchido para identificar a tela");
        code.Text = "BAD";
        Check(Field<Label>(form, "_codeValidation").ForeColor != Color.FromArgb(111, 231, 193),
            "Código incompleto não deve aparecer como válido");
        start.PerformClick();
        Application.DoEvents();
        Check(Field<Label>(form, "_status").Text == "Confira o código", "Código inválido deve ser rejeitado antes de acessar a rede");
        code.Text = "ABC234";
        Check(Field<Label>(form, "_codeValidation").Text.Contains("Código válido"),
            "Código completo deve receber confirmação visual");
        code.Text = "A0I234";
        Check(!Field<Label>(form, "_codeValidation").Text.Contains("Código válido"),
            "Caracteres ausentes no alfabeto da Activity devem ser rejeitados");
        Invoke(form, "ApplyPreset", "Jogo");
        Check(Field<ComboBox>(form, "_quality").Text.Contains("60 FPS"), "Preset Jogo deve selecionar 60 FPS");
        Invoke(form, "ApplyPreset", "Leve");
        Invoke(form, "Lock", true);
        Check(!Field<Button>(form, "_paste").Enabled && !Field<Button>(form, "_refreshSources").Enabled,
            "Configuração bloqueada deve desabilitar Colar e Atualizar");
        Invoke(form, "Lock", false);
        type.GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
        form.Close();
        Console.WriteLine("PASS UI: janela adaptável, código inválido, presets e bloqueio.");
    }

    private static void Capture(Form form, string name)
    {
        Application.DoEvents();
        var start = Field<Button>(form, "_start");
        var bounds = form.RectangleToClient(start.RectangleToScreen(start.ClientRectangle));
        Check(form.ClientRectangle.Contains(bounds), "Botão principal fora da área visível: " + name);
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(Path.Combine("ui-captures", name + ".png"), ImageFormat.Png);
    }

    private static T Field<T>(Form form, string name) => (T)form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Invoke(Form form, string name, params object[] args) => form.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
    private static object? InvokeStatic(Type type, string name, params object[] args) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}
