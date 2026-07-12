using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// The proper settings window: a left category rail and a scrollable pane of labelled controls, each
/// with a one-line description. It edits the shared <see cref="UiConfig"/> in place and pushes every
/// change into the running panel through <see cref="MainWindow.ApplyLive"/> — the exact same live
/// wiring the context menu uses, so there is no second source of truth. Built in code (no XAML) to
/// match the rest of the app and its dark theme.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly MainWindow _host;
    private readonly UiConfig _cfg;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static readonly Brush Accent = Theme.Freeze("#2C63B4");
    private static readonly Brush RailSel = Theme.Freeze("#242423");

    private readonly StackPanel _rail = new() { Margin = new Thickness(6, 8, 6, 8) };
    private readonly ContentControl _pane = new();
    private readonly Dictionary<string, UIElement> _pages = new();
    private readonly List<Button> _railButtons = [];

    public SettingsWindow(MainWindow host)
    {
        _host = host;
        _cfg = host.Config;

        Title = Loc.T("SidebarMonitor — Ajustes");
        Background = Theme.Page;
        Foreground = Theme.InkPrimary;
        Width = 780;
        Height = 600;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildPages();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var railHost = new Border { Background = Theme.Surface, Child = _rail };
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);

        var scroll = new ScrollViewer
        {
            Content = _pane,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(22, 16, 22, 20),
        };
        scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Theme.DarkScrollBar());
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);

        Content = grid;
        Select(_railButtons[0], _pages.Keys.First());
    }

    // ── page registry ─────────────────────────────────────────────────────────────────────────
    private void BuildPages()
    {
        AddPage(Loc.T("Apariencia"), BuildAppearance);
        AddPage(Loc.T("Secciones"), BuildSections);
        AddPage(Loc.T("CPU"), BuildCpu);
        AddPage(Loc.T("Memoria"), BuildMemory);
        AddPage(Loc.T("GPU"), BuildGpu);
        AddPage(Loc.T("Red"), BuildNet);
        AddPage(Loc.T("Discos"), BuildDisks);
        AddPage(Loc.T("Refresco"), BuildRefresh);
        AddPage(Loc.T("Colocación"), BuildPlacement);
        AddPage(Loc.T("Diagnóstico"), BuildDiagnostics);
        AddPage(Loc.T("Actualizaciones"), BuildUpdates);
    }

    private void AddPage(string name, Func<UIElement> build)
    {
        var page = build();
        _pages[name] = page;

        var btn = new Button
        {
            Content = name,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Foreground = Theme.InkSecondary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = Theme.Ui,
            FontSize = 13.5,
            Padding = new Thickness(12, 8, 10, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.Template = FlatButtonTemplate(new CornerRadius(5));
        btn.Click += (_, _) => Select(btn, name);
        _railButtons.Add(btn);
        _rail.Children.Add(btn);
    }

    private void Select(Button btn, string name)
    {
        foreach (var b in _railButtons) { b.Background = Brushes.Transparent; b.Foreground = Theme.InkSecondary; }
        btn.Background = RailSel;
        btn.Foreground = Theme.InkPrimary;
        _pane.Content = _pages[name];
    }

    // ── category pages ────────────────────────────────────────────────────────────────────────
    private UIElement BuildAppearance()
    {
        var p = Page(Loc.T("Apariencia"));
        p.Children.Add(Choice(Loc.T("Idioma"), Loc.T("Idioma de la interfaz. Al cambiarlo se reinicia el panel."),
            [(Loc.T("Automático"), 0), (Loc.T("Español"), 1), (Loc.T("English"), 2)],
            () => _cfg.Language switch { "es" => 1, "en" => 2, _ => 0 },
            v =>
            {
                string lang = v switch { 1 => "es", 2 => "en", _ => "auto" };
                if (lang == _cfg.Language) return;
                _cfg.Language = lang;
                _cfg.Save();
                Restart.Relaunch();   // labels are built once at startup, so re-read them on a fresh launch
            }));
        p.Children.Add(Choice(Loc.T("Tamaño de todas las gráficas"), Loc.T("Alto por defecto de las gráficas. Cada una puede sobreescribirlo abajo."),
            [(Loc.T("Pequeñas"), 100), (Loc.T("Medianas"), 150), (Loc.T("Grandes"), 200), (Loc.T("Enormes"), 300)],
            () => (int)Math.Round(_cfg.GraphScale * 100),
            v => { _cfg.GraphScale = v / 100.0; _host.ApplyLive("graphheights"); }));

        p.Children.Add(SubHeader(Loc.T("Alto por gráfica (sobreescribe el global)")));
        void Override(string label, string key) => p.Children.Add(Choice(label, null,
            [(Loc.T("Global"), -1), (Loc.T("P"), 100), (Loc.T("M"), 150), (Loc.T("G"), 200), (Loc.T("E"), 300)],
            () => _cfg.GraphScales.TryGetValue(key, out double v) ? (int)Math.Round(v * 100) : -1,
            val => { if (val < 0) _cfg.GraphScales.Remove(key); else _cfg.GraphScales[key] = val / 100.0; _host.ApplyLive("graphheights"); }));
        Override(Loc.T("CPU"), "cpu");
        Override(Loc.T("GPU"), "gpu");
        Override(Loc.T("Red"), "net");
        Override(Loc.T("Discos"), "disk");

        p.Children.Add(SubHeader(Loc.T("Auto-escala del eje Y")));
        p.Children.Add(Note(Loc.T("Ajusta cada eje al mín/máx de su ventana para ver el detalle cuando los valores son bajos.")));
        p.Children.Add(Toggle(Loc.T("CPU"), null, () => _cfg.CpuGraphAuto, v => { _cfg.CpuGraphAuto = v; _host.ApplyLive("autoscale"); }));
        p.Children.Add(Toggle(Loc.T("GPU"), null, () => _cfg.GpuGraphAuto, v => { _cfg.GpuGraphAuto = v; _host.ApplyLive("autoscale"); }));
        p.Children.Add(Toggle(Loc.T("Red"), null, () => _cfg.NetGraphAuto, v => { _cfg.NetGraphAuto = v; _host.ApplyLive("autoscale"); }));
        p.Children.Add(Toggle(Loc.T("Discos"), null, () => _cfg.DiskGraphAuto, v => { _cfg.DiskGraphAuto = v; _host.ApplyLive("autoscale"); }));

        p.Children.Add(SubHeader(Loc.T("Colores")));
        var palettes = Theme.CorePaletteNames.Select((name, i) => (name, i)).ToArray();
        p.Children.Add(Choice(Loc.T("Colores de núcleos"), Loc.T("Paleta para las barras, líneas y mini-gráficas por núcleo."),
            palettes, () => _cfg.CorePalette, v => { _cfg.CorePalette = v; _host.ApplyLive("palette"); }));

        p.Children.Add(SubHeader(Loc.T("Tooltips")));
        p.Children.Add(Slider(Loc.T("Opacidad de tooltips"), Loc.T("Transparencia del fondo de los tooltips (1 = opaco)."),
            0.5, 1.0, 0.05, () => _cfg.TooltipOpacity, v => { _cfg.TooltipOpacity = v; _host.ApplyLive("tooltip"); }, "P0"));
        return p;
    }

    private StackPanel? _orderPanel;

    private UIElement BuildSections()
    {
        var p = Page(Loc.T("Secciones"));
        p.Children.Add(Note(Loc.T("Muestra u oculta cada sección.")));
        foreach (var s in _host.SectionsRO)
        {
            var sec = s;
            p.Children.Add(Toggle(sec.Title, null,
                () => sec.Visibility == Visibility.Visible,
                v => _host.SetSectionShown(sec, v)));
        }

        p.Children.Add(SubHeader(Loc.T("Orden (arriba = primera)")));
        _orderPanel = new StackPanel();
        p.Children.Add(_orderPanel);
        PopulateOrder();
        return p;
    }

    private void PopulateOrder()
    {
        if (_orderPanel is null) return;
        _orderPanel.Children.Clear();
        var ordered = _host.OrderedSectionsRO();
        for (int i = 0; i < ordered.Count; i++)
        {
            var sec = ordered[i];
            int idx = i;

            var label = new TextBlock { Text = $"{i + 1}.  {sec.Title}", Foreground = Theme.InkPrimary, FontFamily = Theme.Ui, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var up = ArrowButton("▲", idx > 0, () => { _host.MoveSectionLive(sec.Key, -1); PopulateOrder(); });
            var down = ArrowButton("▼", idx < ordered.Count - 1, () => { _host.MoveSectionLive(sec.Key, +1); PopulateOrder(); });
            buttons.Children.Add(up);
            buttons.Children.Add(down);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(label);
            grid.Children.Add(buttons);
            _orderPanel.Children.Add(new Border { Child = grid, Padding = new Thickness(0, 5, 0, 5) });
        }
    }

    private Button ArrowButton(string glyph, bool enabled, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            IsEnabled = enabled,
            Foreground = enabled ? Theme.InkSecondary : Theme.InkMuted,
            Background = Theme.Grid,
            FontFamily = Theme.Ui,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Template = FlatButtonTemplate(new CornerRadius(4));
        b.Click += (_, _) => onClick();
        return b;
    }

    private UIElement BuildRefresh()
    {
        var p = Page(Loc.T("Refresco"));
        (string L, int V)[] rates = [(Loc.T("0,5 s"), 500), (Loc.T("1 s"), 1000), (Loc.T("2 s"), 2000), (Loc.T("5 s"), 5000)];
        p.Children.Add(Choice(Loc.T("Global"), Loc.T("Ritmo de muestreo por defecto. Reinicia el agente para muestrear al nuevo ritmo."),
            rates.Select(r => (r.L, r.V)).ToArray(), () => _cfg.RefreshMs, v => _host.SetGlobalRefresh(v)));

        p.Children.Add(SubHeader(Loc.T("Por sección (sobreescribe el global)")));
        p.Children.Add(Note(Loc.T("«Global» = seguir el ritmo de arriba. Útil para tener la CPU rápida y los discos lentos.")));
        (string L, int V)[] perOpts = [(Loc.T("Global"), -1), .. rates];
        foreach (var s in _host.SectionsRO)
        {
            string key = s.Key;
            p.Children.Add(Choice(s.Title, null, perOpts,
                () => _cfg.SectionRefreshMs.TryGetValue(key, out int ms) ? ms : -1,
                v => _host.SetSectionRefresh(key, v)));
        }
        return p;
    }

    private UIElement BuildCpu()
    {
        var p = Page(Loc.T("CPU"));
        p.Children.Add(Choice(Loc.T("Gráfica principal"), Loc.T("Una línea de uso total, líneas por núcleo superpuestas, o una mini-gráfica por núcleo en rejilla."),
            [(Loc.T("Total"), 0), (Loc.T("Superpuesta"), 1), (Loc.T("Separada"), 2)], () => _cfg.CpuGraphMode,
            v => { _cfg.CpuGraphMode = v; _cfg.CpuPerCoreGraph = v == 1; _host.ApplyLive("cpugraph"); }));
        p.Children.Add(Choice(Loc.T("Columnas (gráfica separada)"), Loc.T("Cuántas mini-gráficas por fila en el modo «Separada»."),
            [("4", 4), ("3", 3), ("2", 2), ("1", 1)], () => _cfg.CpuGraphColumns,
            v => { _cfg.CpuGraphColumns = v; _host.ApplyLive("cpucols"); }));
        p.Children.Add(Choice(Loc.T("Eje Y de las mini-gráficas"), Loc.T("Fijo 0-100 = todos los núcleos comparables de un vistazo. Autoescala = cada núcleo a su rango (detalle en núcleos ociosos, pero no comparable)."),
            [(Loc.T("Fijo 0-100"), 0), (Loc.T("Autoescala"), 1)], () => _cfg.CpuGridAutoScale ? 1 : 0,
            v => { _cfg.CpuGridAutoScale = v == 1; _host.ApplyLive("cpugridaxis"); }));

        p.Children.Add(Choice(Loc.T("Frecuencia mostrada (GHz)"), Loc.T("Qué agregado del reloj por núcleo se muestra arriba."),
            [(Loc.T("Mejor"), 0), (Loc.T("Media"), 1), (Loc.T("Mediana"), 2)], () => _cfg.CpuFreqMode,
            v => { _cfg.CpuFreqMode = v; _host.ApplyLive("freqcaption"); }));

        p.Children.Add(SubHeader(Loc.T("Filas por núcleo")));
        p.Children.Add(Toggle(Loc.T("Mostrar frecuencia"), null, () => _cfg.ShowCoreFreq, v => { _cfg.ShowCoreFreq = v; _host.ApplyLive("cpugraph"); }));
        p.Children.Add(Toggle(Loc.T("Mostrar temperatura"), Loc.T("Del SDK de AMD; colorea hacia rojo cerca del Tjmax."), () => _cfg.ShowCoreTemp, v => { _cfg.ShowCoreTemp = v; _host.ApplyLive("cpugraph"); }));
        p.Children.Add(Choice(Loc.T("Posición de la métrica"), Loc.T("Dónde va la frecuencia/temperatura en la fila."),
            [(Loc.T("Dentro"), 0), (Loc.T("Al final"), 1), (Loc.T("Fuera"), 2)], () => _cfg.CoreMetricPos,
            v => { _cfg.CoreMetricPos = v; _host.ApplyLive("cpugraph"); }));
        p.Children.Add(Choice(Loc.T("Barra por núcleo"), Loc.T("Uso (%Util), residencia C0 (despierto), ambas superpuestas, o uso + marca de C0."),
            [(Loc.T("Uso"), 0), ("C0", 1), (Loc.T("Combinada"), 2), (Loc.T("Uso+tick"), 3)], () => _cfg.CoreBarMode,
            v => { _cfg.CoreBarMode = v; _host.ApplyLive("cpugraph"); }));
        p.Children.Add(Toggle(Loc.T("Marcar núcleos dormidos"), Loc.T("Atenúa y etiqueta «sleep» los núcleos aparcados (C0≈0), como Ryzen Master."), () => _cfg.MarkSleepCores, v => { _cfg.MarkSleepCores = v; _host.ApplyLive("cpugraph"); }));

        p.Children.Add(SubHeader(Loc.T("Modelo e indicadores")));
        p.Children.Add(Choice(Loc.T("Modelo de CPU"), Loc.T("Dónde mostrar el nombre del procesador."),
            [(Loc.T("No"), 0), (Loc.T("En título"), 1), (Loc.T("Dentro"), 2)], () => _cfg.CpuNameMode, v => { _cfg.CpuNameMode = v; _cfg.Save(); }));
        p.Children.Add(Toggle(Loc.T("Indicador de throttle (POT/CORR/TÉRM)"), Loc.T("Qué tope duro frena el boost ahora. Del SDK de AMD."), () => _cfg.ShowThrottle, v => { _cfg.ShowThrottle = v; _cfg.Save(); }));
        p.Children.Add(Toggle(Loc.T("Boost logrado / pico"), Loc.T("Frecuencia del mejor núcleo vs su pico de sesión."), () => _cfg.ShowBoost, v => { _cfg.ShowBoost = v; _cfg.Save(); }));
        p.Children.Add(Toggle(Loc.T("Mostrar VID (voltaje)"), null, () => _cfg.ShowCpuVid, v => { _cfg.ShowCpuVid = v; _cfg.Save(); }));
        p.Children.Add(Toggle(Loc.T("Mostrar límites (PPT/TDC/EDC/térmico)"), null, () => _cfg.ShowCpuLimits, v => { _cfg.ShowCpuLimits = v; _cfg.Save(); }));
        return p;
    }

    private UIElement BuildMemory()
    {
        var p = Page(Loc.T("Memoria"));
        p.Children.Add(Choice(Loc.T("Información de módulos"), Loc.T("Datos de los módulos de RAM leídos por WMI (SMBIOS), sin elevación."),
            [(Loc.T("No"), 0), (Loc.T("Resumen"), 1), (Loc.T("Detalle"), 2)], () => _cfg.RamModulesMode,
            v => { _cfg.RamModulesMode = v; _host.ApplyLive("ram"); }));
        p.Children.Add(Note(Loc.T("Resumen: «2× 16 GiB DDR5-6000». Detalle: una línea por módulo con ranura, tamaño, tipo, frecuencia (MT/s), fabricante y part number. En cualquier caso el detalle está también en el tooltip.")));
        p.Children.Add(Choice(Loc.T("Unidades del uso"), Loc.T("Binario (GiB, 1024) o decimal (GB, 1000) para el uso/total del sistema. El tamaño de los módulos va siempre en GiB (su tamaño real)."),
            [(Loc.T("Binario"), 1), (Loc.T("Decimal"), 0)], () => _cfg.MemUnitsBinary ? 1 : 0,
            v => { _cfg.MemUnitsBinary = v == 1; _host.ApplyLive("ram"); }));
        return p;
    }

    private UIElement BuildGpu()
    {
        var p = Page(Loc.T("GPU"));
        p.Children.Add(Choice(Loc.T("Mostrar"), Loc.T("Qué GPU(s) ver. La iGPU solo da % y motores (sin sensores propios)."),
            [("NVIDIA", 0), ("AMD iGPU", 1), (Loc.T("Ambas"), 2)], () => _cfg.GpuView,
            v => { _cfg.GpuView = v; _host.ApplyLive("gpuview"); }));
        p.Children.Add(Toggle(Loc.T("Motores (mini-gráficas)"), Loc.T("Una mini-gráfica por motor: 3D, compute/ML, decode/encode…"), () => _cfg.ShowGpuEngines, v => { _cfg.ShowGpuEngines = v; _host.ApplyLive("gpuengines"); }));
        p.Children.Add(Choice(Loc.T("Columnas de motores"), Loc.T("Cuántas mini-gráficas por fila."),
            [("4", 4), ("3", 3), ("2", 2), ("1", 1)], () => _cfg.GpuEngineColumns,
            v => { _cfg.GpuEngineColumns = v; _host.ApplyLive("gpucols"); }));
        p.Children.Add(Choice(Loc.T("Modelo de GPU"), Loc.T("Dónde mostrar el nombre de la GPU primaria."),
            [(Loc.T("No"), 0), (Loc.T("En título"), 1)], () => _cfg.GpuNameMode == 2 ? 0 : _cfg.GpuNameMode,
            v => { _cfg.GpuNameMode = v; _cfg.Save(); }));
        return p;
    }

    private UIElement BuildNet()
    {
        var p = Page(Loc.T("Red"));
        p.Children.Add(Choice(Loc.T("Filas de procesos por ancho de banda"), Loc.T("Número fijo de filas en la sección RED (fijo a propósito)."),
            [(Loc.T("Ninguna"), 0), ("3", 3), ("4", 4), ("6", 6), ("8", 8)], () => _cfg.NetProcRows,
            v => { _cfg.NetProcRows = v; _host.ApplyLive("netrows"); }));
        p.Children.Add(Choice(Loc.T("Unidades"), Loc.T("Binario (KiB/MiB, 1024) o decimal (KB/MB, 1000)."),
            [(Loc.T("Binario"), 1), (Loc.T("Decimal"), 0)], () => _cfg.NetUnitsBinary ? 1 : 0,
            v => { _cfg.NetUnitsBinary = v == 1; _cfg.Save(); }));
        return p;
    }

    private UIElement BuildDisks()
    {
        var p = Page(Loc.T("Discos"));
        p.Children.Add(Toggle(Loc.T("Ocultar discos virtuales"), null, () => _cfg.HideVirtualDisks, v => { _cfg.HideVirtualDisks = v; _host.ApplyLive("disks"); }));
        p.Children.Add(Toggle(Loc.T("Ocultar discos extraíbles"), null, () => _cfg.HideRemovableDisks, v => { _cfg.HideRemovableDisks = v; _host.ApplyLive("disks"); }));
        p.Children.Add(Toggle(Loc.T("Ocultar disco del sistema"), null, () => _cfg.HideSystemDisk, v => { _cfg.HideSystemDisk = v; _host.ApplyLive("disks"); }));
        p.Children.Add(Choice(Loc.T("Unidades de las tasas"), Loc.T("Binario (KiB/MiB) o decimal (KB/MB) para las velocidades de lectura/escritura. La capacidad va siempre en decimal (como se anuncian los discos)."),
            [(Loc.T("Binario"), 1), (Loc.T("Decimal"), 0)], () => _cfg.DiskUnitsBinary ? 1 : 0,
            v => { _cfg.DiskUnitsBinary = v == 1; _cfg.Save(); }));
        return p;
    }

    private UIElement BuildPlacement()
    {
        var p = Page(Loc.T("Colocación"));
        p.Children.Add(Toggle(Loc.T("Siempre encima"), null, () => _cfg.Topmost, v => { _cfg.Topmost = v; _host.ApplyLive("placement"); }));
        p.Children.Add(Toggle(Loc.T("Anclado al borde"), Loc.T("Anclado lo pega a un borde; flotante se arrastra por su cabecera."), () => _cfg.Docked, v => { _cfg.Docked = v; _host.ApplyLive("placement"); }));
        p.Children.Add(Toggle(Loc.T("Reservar espacio (empuja ventanas)"), Loc.T("Solo anclado. Reserva la franja para que nada la tape; maximizar/snap se paran en su borde."), () => _cfg.ReserveSpace, v => { _cfg.ReserveSpace = v; _host.ApplyLive("placement"); }));
        p.Children.Add(Toggle(Loc.T("Borde izquierdo"), Loc.T("Anclar al borde izquierdo en vez del derecho."), () => _cfg.EdgeLeft, v => { _cfg.EdgeLeft = v; _host.ApplyLive("placement"); }));
        p.Children.Add(Toggle(Loc.T("Ignorar clics (pasan a través)"), Loc.T("Los clics atraviesan el panel. Reactívalo desde la bandeja."), () => _cfg.ClickThrough, v => { _cfg.ClickThrough = v; _host.ApplyLive("clickthrough"); }));

        var mons = _host.MonitorsRO;
        if (mons.Count > 1)
        {
            var opts = new (string, int)[mons.Count];
            for (int i = 0; i < mons.Count; i++)
            {
                var r = mons[i].Info.rcMonitor;
                opts[i] = ($"{i + 1}: {r.Width}×{r.Height}{(mons[i].Info.dwFlags == 1 ? " ★" : "")}", i);
            }
            p.Children.Add(Choice(Loc.T("Monitor"), Loc.T("En qué pantalla vive el panel."), opts,
                () => Math.Clamp(_cfg.Monitor, 0, mons.Count - 1),
                v => { _cfg.Monitor = v; _host.ApplyLive("placement"); }));
        }

        p.Children.Add(Slider(Loc.T("Ancho del panel"), Loc.T("Ancho en píxeles (también se arrastra el borde interior)."),
            240, 900, 5, () => _cfg.Width, v => { _cfg.Width = (int)Math.Round(v); _host.ApplyLive("placement"); }, "F0", "px"));
        return p;
    }

    private UIElement BuildDiagnostics()
    {
        var p = Page(Loc.T("Diagnóstico"));
        p.Children.Add(Toggle(Loc.T("Registrar a CSV"), Loc.T("Graba una fila por muestra (CPU, límites, por-núcleo, RAM, GPU0, red, disco) a %LOCALAPPDATA%\\SidebarMonitor\\logs."), () => _cfg.LogCsv, v => { _cfg.LogCsv = v; _host.ApplyLive("csv"); }));
        p.Children.Add(Toggle(Loc.T("Datos de depuración (overlay)"), Loc.T("Bajo el título: fabricante/modelo de CPU, versiones del contrato, estado del SDK/helper, cadencia y estado del CSV."), () => _cfg.LogVerbose, v => { _cfg.LogVerbose = v; _host.ApplyLive("verbose"); }));

        var open = TextButton(Loc.T("Abrir carpeta de logs"));
        open.Click += (_, _) =>
        {
            try { System.IO.Directory.CreateDirectory(CsvLogger.LogDir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CsvLogger.LogDir) { UseShellExecute = true }); }
            catch { }
        };
        p.Children.Add(open);
        return p;
    }

    private UIElement BuildUpdates()
    {
        var p = Page(Loc.T("Actualizaciones"));
        p.Children.Add(Toggle(Loc.T("Buscar actualizaciones automáticamente"),
            Loc.T("Al arrancar (y a diario) consulta GitHub Releases. Solo se contacta la API pública de GitHub; no se envía nada tuyo."),
            () => _cfg.CheckUpdates, v => { _cfg.CheckUpdates = v; _cfg.Save(); }));

        p.Children.Add(SubHeader(Loc.T("Versión actual: {0}", _host.CurrentVersionText)));

        var status = new TextBlock
        {
            Foreground = Theme.InkSecondary, FontFamily = Theme.Ui, FontSize = 12.5,
            Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        var check = TextButton(Loc.T("Buscar ahora"));
        check.Click += (_, _) => _host.RunUpdateCheck(true, s => status.Text = s);
        var apply = TextButton(Loc.T("Actualizar ahora"));
        apply.Margin = new Thickness(8, 12, 0, 0);
        apply.Click += (_, _) => _host.ApplyUpdate();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(check);
        row.Children.Add(apply);
        p.Children.Add(row);
        p.Children.Add(status);
        return p;
    }

    // ── control framework ─────────────────────────────────────────────────────────────────────
    private static StackPanel Page(string title)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Theme.InkPrimary,
            FontFamily = Theme.Ui,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });
        return p;
    }

    private static TextBlock SubHeader(string text) => new()
    {
        Text = text,
        Foreground = Theme.InkSecondary,
        FontFamily = Theme.Ui,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 16, 0, 4),
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = Theme.InkMuted,
        FontFamily = Theme.Ui,
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBlock Desc(string text) => new()
    {
        Text = text,
        Foreground = Theme.InkMuted,
        FontFamily = Theme.Ui,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 0),
    };

    /// <summary>A labelled checkbox row with an optional description under the label.</summary>
    private Border Toggle(string label, string? desc, Func<bool> get, Action<bool> set)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, Foreground = Theme.InkPrimary, FontFamily = Theme.Ui, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (desc is not null) text.Children.Add(Desc(desc));

        var check = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        check.Checked += (_, _) => set(true);
        check.Unchecked += (_, _) => set(false);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(check, 1);
        grid.Children.Add(text);
        grid.Children.Add(check);

        // Clicking anywhere on the row toggles it.
        var row = new Border { Child = grid, Background = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseLeftButtonUp += (_, _) => check.IsChecked = !(check.IsChecked ?? false);
        return row;
    }

    /// <summary>A labelled segmented control (row of buttons; one selected).</summary>
    private Border Choice(string label, string? desc, (string Label, int Value)[] options, Func<int> get, Action<int> set)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, Foreground = Theme.InkPrimary, FontFamily = Theme.Ui, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (desc is not null) text.Children.Add(Desc(desc));

        var seg = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new List<(Button Btn, int Val)>();
        void Restyle()
        {
            int cur = get();
            foreach (var (btn, val) in buttons)
            {
                bool on = val == cur;
                btn.Background = on ? Accent : Theme.Grid;
                btn.Foreground = on ? Theme.InkPrimary : Theme.InkSecondary;
            }
        }
        foreach (var (lbl, val) in options)
        {
            var b = new Button
            {
                Content = lbl,
                FontFamily = Theme.Ui,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Template = FlatButtonTemplate(new CornerRadius(4));
            b.Click += (_, _) => { set(val); Restyle(); };
            buttons.Add((b, val));
            seg.Children.Add(b);
        }
        Restyle();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(seg, 1);
        grid.Children.Add(text);
        grid.Children.Add(seg);
        return new Border { Child = grid, Padding = new Thickness(0, 8, 0, 8) };
    }

    /// <summary>A labelled slider with a live value readout.</summary>
    private Border Slider(string label, string? desc, double min, double max, double step,
        Func<double> get, Action<double> set, string fmt, string unit = "")
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, Foreground = Theme.InkPrimary, FontFamily = Theme.Ui, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (desc is not null) text.Children.Add(Desc(desc));

        var readout = new TextBlock { Foreground = Theme.InkSecondary, FontFamily = Theme.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        string Fmt(double v) => (fmt == "P0" ? (v * 100).ToString("F0", Ci) + "%" : v.ToString(fmt, Ci) + (unit.Length > 0 ? " " + unit : ""));
        readout.Text = Fmt(get());

        var slider = new Slider { Minimum = min, Maximum = max, Value = get(), Width = 200, SmallChange = step, LargeChange = step * 4, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { readout.Text = Fmt(e.NewValue); set(e.NewValue); };

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(readout);
        right.Children.Add(slider);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(text);
        grid.Children.Add(right);
        return new Border { Child = grid, Padding = new Thickness(0, 8, 0, 8) };
    }

    private Button TextButton(string label)
    {
        var b = new Button
        {
            Content = label,
            Foreground = Theme.InkPrimary,
            Background = Theme.Grid,
            FontFamily = Theme.Ui,
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Template = FlatButtonTemplate(new CornerRadius(5));
        return b;
    }

    /// <summary>A flat button template (no Aero chrome) that honours Background/CornerRadius.</summary>
    private static ControlTemplate FlatButtonTemplate(CornerRadius radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.CornerRadiusProperty, radius);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding("Padding")
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.AppendChild(cp);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
