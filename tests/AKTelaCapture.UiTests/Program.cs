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
        Check(form.FormBorderStyle == FormBorderStyle.FixedSingle,
            "A janela principal não deve permitir redimensionamento pelo usuário");
        Check(!form.MaximizeBox, "A janela vertical não deve oferecer maximização");
        Check(form.SizeGripStyle == SizeGripStyle.Hide, "A alça de redimensionamento deve ficar oculta");
        Directory.CreateDirectory("ui-captures");
        Capture(form, "desktop");
        var start = Field<Button>(form, "_start");
        var code = Field<TextBox>(form, "_code");
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
        Console.WriteLine("PASS UI: janela retrato fixa, código inválido, presets e bloqueio.");
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
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}
