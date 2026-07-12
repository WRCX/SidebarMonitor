using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// Shown once, on first run, and branched by CPU vendor:
///  • AMD  → present the Ryzen Master Monitoring SDK EULA; the deep CPU sensors (temp, watts, C0,
///           per-core boost) stay off until the user accepts (AMD's redistribution condition).
///  • Intel → explain that equivalent CPU sensors need a ring0 driver (PawnIO) that isn't bundled
///           yet, so the app runs in PDH-only mode. No AMD EULA is ever shown to an Intel user.
///  • other → no dialog; just record that first-run ran.
/// The user's answer is persisted in <see cref="UiConfig"/> and, for AMD, mirrored to the marker
/// file the elevated helper reads (<see cref="ConsentMarker"/>).
/// </summary>
internal static class FirstRunDialog
{
    /// <summary>Show the notice if it hasn't been shown, then keep the SDK marker in sync with config.</summary>
    public static void EnsureShown(UiConfig cfg)
    {
        if (!cfg.FirstRunNoticeShown)
        {
            try { ShowFor(cfg); }
            catch { /* never let a dialog failure stop the app starting */ }
            cfg.FirstRunNoticeShown = true;
            cfg.Save();
        }

        // Every launch: the marker the helper reads must reflect the stored consent (covers a
        // deleted marker, a config copied between machines, or a future settings toggle).
        ConsentMarker.SetAmdSdk(cfg.AmdEulaAccepted);
    }

    private static void ShowFor(UiConfig cfg)
    {
        switch (CpuVendor.Maker)
        {
            case CpuMaker.Amd: ShowAmd(cfg); break;
            case CpuMaker.Intel: ShowIntel(cfg); break;
            default: break;   // unknown vendor: PDH-only, nothing to consent to
        }
    }

    /// <summary>Preview a branch without touching the real config (for screenshots/QA). "intel" forces
    /// the Intel notice even on an AMD box; anything else shows the AMD EULA branch.</summary>
    public static void Preview(string which)
    {
        var throwaway = new UiConfig { Ephemeral = true };
        if (string.Equals(which, "intel", StringComparison.OrdinalIgnoreCase)) ShowIntel(throwaway);
        else ShowAmd(throwaway);
    }

    // ── AMD: EULA + accept/decline ────────────────────────────────────────────────────────────
    private static void ShowAmd(UiConfig cfg)
    {
        var body = new StackPanel();
        body.Children.Add(Para(
            "Para leer temperatura, vatios, residencia C0 y el boost por núcleo de tu Ryzen, " +
            "SidebarMonitor usa el AMD Ryzen Master Monitoring SDK: el driver oficial de AMD, " +
            "firmado y compatible con Integridad de Memoria (HVCI). No usa WinRing0 ni drivers dudosos."));
        body.Children.Add(Para(
            "AMD exige que aceptes la licencia (EULA) de su SDK antes de utilizarlo. Es un software " +
            "«de evaluación», se ofrece sin garantía y con responsabilidad limitada por parte de AMD."));
        body.Children.Add(Para(
            "Si no la aceptas, la app funciona igual pero en modo básico: uso y frecuencia por núcleo " +
            "vía Windows (PDH), más red, discos y GPU. Podrás cambiar de opinión más adelante."));

        var eulaBtn = LinkButton("Ver la licencia completa de AMD (License.rtf) »", OpenAmdEula);
        body.Children.Add(eulaBtn);

        var accept = new CheckBox
        {
            Content = "He leído y acepto la licencia del SDK de monitorización de AMD.",
            Foreground = Theme.InkPrimary,
            FontFamily = Theme.Ui,
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 0),
        };
        body.Children.Add(accept);

        var win = MakeWindow("SidebarMonitor — activar sensores de tu Ryzen", body,
            out var buttons, out _);

        var ok = PrimaryButton("Aceptar y activar sensores");
        ok.IsEnabled = false;
        accept.Checked += (_, _) => ok.IsEnabled = true;
        accept.Unchecked += (_, _) => ok.IsEnabled = false;
        var skip = SecondaryButton("Seguir sin el SDK (modo básico)");

        ok.Click += (_, _) =>
        {
            cfg.AmdEulaAccepted = true;
            ConsentMarker.SetAmdSdk(true);
            win.DialogResult = true;
        };
        skip.Click += (_, _) =>
        {
            cfg.AmdEulaAccepted = false;
            ConsentMarker.SetAmdSdk(false);
            win.DialogResult = true;
        };
        buttons.Children.Add(skip);
        buttons.Children.Add(ok);

        win.ShowDialog();
    }

    // ── Intel: ring0 notice, no consent to give ───────────────────────────────────────────────
    private static void ShowIntel(UiConfig cfg)
    {
        var body = new StackPanel();
        body.Children.Add(Para(
            "Tu CPU es Intel. A diferencia de AMD (que publica un SDK oficial firmado), Intel no " +
            "ofrece una vía oficial y firmada para leer la temperatura y los vatios del procesador."));
        body.Children.Add(Para(
            "Esos sensores viven en registros del chip (MSR) a los que solo se llega con un driver a " +
            "nivel de kernel (ring 0), como PawnIO. SidebarMonitor todavía no lo incluye, así que por " +
            "ahora el detalle profundo de la CPU (temp/vatios/C0/boost por núcleo) no está disponible."));
        body.Children.Add(Para(
            "Lo que sí verás con normalidad: uso y frecuencia por núcleo (Windows PDH), procesos, red, " +
            "discos y su temperatura, y la GPU — incluida temperatura/vatios si el driver de tu GPU " +
            "(NVIDIA / AMD / Intel) los expone."));
        body.Children.Add(Para(
            "El soporte de sensores Intel (vía ring0/PawnIO) está en la hoja de ruta. Cuando llegue, " +
            "este mismo aviso te pedirá permiso para instalar ese componente."));

        var win = MakeWindow("SidebarMonitor — nota sobre CPUs Intel", body, out var buttons, out _);
        var ok = PrimaryButton("Entendido, continuar");
        ok.Click += (_, _) => { cfg.IntelRing0Ack = true; win.DialogResult = true; };
        buttons.Children.Add(ok);
        win.ShowDialog();
    }

    // ── shared chrome ─────────────────────────────────────────────────────────────────────────
    private static Window MakeWindow(string title, UIElement body, out StackPanel buttons, out TextBlock head)
    {
        var root = new StackPanel { Margin = new Thickness(24) };

        head = new TextBlock
        {
            Text = title,
            Foreground = Theme.InkPrimary,
            FontFamily = Theme.Ui,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        root.Children.Add(head);

        string brand = CpuVendor.Brand;
        if (brand.Length > 0)
            root.Children.Add(new TextBlock
            {
                Text = brand,
                Foreground = Theme.InkMuted,
                FontFamily = Theme.Ui,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 14),
            });

        root.Children.Add(body);

        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        root.Children.Add(buttons);

        return new Window
        {
            Title = "SidebarMonitor",
            Content = root,
            Background = Theme.Page,
            Foreground = Theme.InkPrimary,
            SizeToContent = SizeToContent.Height,
            Width = 600,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
        };
    }

    private static TextBlock Para(string text) => new()
    {
        Text = text,
        Foreground = Theme.InkSecondary,
        FontFamily = Theme.Ui,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
        LineHeight = 19,
    };

    private static Button PrimaryButton(string text) => StyledButton(text, Theme.Freeze("#2C63B4"), Theme.InkPrimary);
    private static Button SecondaryButton(string text) => StyledButton(text, Theme.Grid, Theme.InkSecondary);

    private static Button StyledButton(string text, Brush bg, Brush fg)
    {
        var b = new Button
        {
            Content = text,
            Foreground = fg,
            Background = bg,
            FontFamily = Theme.Ui,
            FontSize = 13,
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        // Flat template so the button doesn't get the default grey Aero chrome over our dark theme.
        var border = new System.Windows.FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 7, 16, 7));
        border.AppendChild(cp);
        b.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        b.Padding = new Thickness(0);
        return b;
    }

    private static UIElement LinkButton(string text, Action onClick)
    {
        var tb = new TextBlock
        {
            Foreground = Theme.Freeze("#5B9BFF"),
            FontFamily = Theme.Ui,
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 4, 0, 0),
            TextDecorations = TextDecorations.Underline,
        };
        tb.Inlines.Add(new Run(text));
        tb.MouseLeftButtonUp += (_, _) => onClick();
        return tb;
    }

    private static void OpenAmdEula()
    {
        string? path = FindAmdEula();
        if (path is null)
        {
            MessageBox.Show(
                "No encuentro License.rtf empaquetada. Está en la instalación del SDK de AMD, " +
                @"normalmente en C:\Program Files\AMD\RyzenMasterMonitoringSDK\License.rtf.",
                "Licencia de AMD", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* no default handler for .rtf, ignore */ }
    }

    private static string? FindAmdEula()
    {
        foreach (var p in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "License.rtf"),
            Path.Combine(AppContext.BaseDirectory, "RyzenSdk", "License.rtf"),
            @"C:\Program Files\AMD\RyzenMasterMonitoringSDK\License.rtf",
        })
            if (File.Exists(p)) return p;
        return null;
    }
}
