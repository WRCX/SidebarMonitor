using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

internal sealed partial class MainWindow : AppBarWindow
{
    private readonly UiConfig _cfg;
    private readonly List<(IntPtr Handle, MonitorInfo Info)> _monitors;

    private readonly DispatcherTimer _timer = new();
    // Coalesces the burst of WM_DISPLAYCHANGE messages a monitor off/on emits, and waits for the
    // work area to settle, before re-placing once.
    private readonly DispatcherTimer _displaySettle = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Dictionary<string, long> _sectionLastTick = [];   // per-section refresh gating
    private SeqLockReader<Snapshot>? _reader;
    private System.Diagnostics.Process? _ownedAgent;
    private DateTime _nextAgentLaunch = DateTime.MinValue;   // backoff gate for (re)launching the agent
    private int _agentLaunchStreak;                          // consecutive launches without healthy data
    private TrayIcon? _tray;

    private readonly List<Section> _sections = [];
    private readonly Dictionary<string, Section> _byKey = [];   // O(1) Find (called ~18× per tick)
    private readonly StackPanel _stack = new();
    private readonly TextBlock _status;
    private readonly CsvLogger _csv = new();
    private readonly TextBlock _debug = new()
    {
        FontFamily = Theme.Mono,
        FontSize = 9.5,
        Foreground = Theme.InkMuted,
        Margin = new Thickness(8, 0, 8, 4),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly ScrollViewer _body;
    private readonly Border _tab;
    private readonly TextBlock _tabArrow;
    private readonly TextBlock _minButton;
    private readonly Border _titleBar;

    // CPU
    private readonly TextBlock _cpuName = Theme.Text("", 10, Theme.InkMuted);
    private readonly TextBlock _cpuFreq = Stat();
    private readonly TextBlock _cpuFreqCaption = Theme.Text("GHz", 9, Theme.InkMuted);
    private readonly TextBlock _cpuWatts = Stat();
    private readonly TextBlock _cpuTemp = Stat();
    private readonly TextBlock _cpuFan = Stat();
    // The fan tile's caption is dynamic: "%vent" for EC-duty models, or "· NNNN rpm" on HP WMI models
    // where the value carries the % ("47% · 2700 rpm").
    private readonly TextBlock _cpuFanCap = Theme.Text("%vent", 9, Theme.InkMuted);
    private readonly TextBlock _cpuPct;
    private readonly Sparkline _cpuSpark = new(Theme.SeriesCpu) { FixedMax = 100 };
    private readonly CoreSparkline _cpuCoreSpark = new(height: 48);
    private readonly CoreGrid _cpuCoreGrid = new();
    private readonly Grid _cpuGraphHost = new();
    private readonly TextBlock _cpuExtra = Muted();
    private readonly TextBlock _cpuThrottle = Muted();
    private readonly TextBlock _cpuBoost = Muted();
    // Highest best-core clock seen this session — the chip's real peak boost (≈ rated Fmax when
    // cool), used as the "nominal" the current boost is measured against.
    private double _bestFreqPeakMhz;
    private readonly CoreRows _coreRows = new();

    // RAM
    private readonly BarMeter _ramMeter = new(Theme.SeriesCpu);
    private readonly TextBlock _ramText = Value();
    private readonly TextBlock _commitText = Muted();
    private readonly TextBlock _ramModules = Muted();

    // GPU — a self-contained widget: selector for discrete/integrated/both, per-engine mini-graphs.
    private readonly GpuSection _gpuSection = new();
    private readonly GameSection _gameSection = new();
    public bool TestFakeFps { get; init; }   // set by --fake-fps to preview the GAME section

    // NET
    private readonly Sparkline _netSpark = new(Theme.SeriesIn, Theme.SeriesOut);
    private readonly TextBlock _netDl = Value();
    private readonly TextBlock _netUl = Value();
    private readonly TextBlock _netPrimary = Muted();
    private readonly StackPanel _netProcRows = new();

    // DISK
    private readonly StackPanel _diskPanels = new();
    private readonly List<DiskBlock> _diskBlocks = [];

    private sealed record DiskBlock(string Key, TextBlock Head, TextBlock Temp, TextBlock Volumes, TextBlock Sub,
                                    BarMeter Active, TextBlock ActiveText, Sparkline Spark, TextBlock Rates);

    // TOP
    private readonly (TextBlock Name, TextBlock Cpu, TextBlock Mem)[] _topRows;
    private readonly TextBlock _totals = Muted();

    // DOCKER / WSL — read from inside the guest, since Windows only sees the vmmemWSL aggregate.
    private readonly DockerCollector _docker = new();
    private readonly WslCollector _wsl = new();
    private readonly StackPanel _dockerRows = new();
    private readonly StackPanel _wslRows = new();

    public MainWindow(UiConfig cfg, List<(IntPtr, MonitorInfo)> monitors)
    {
        _cfg = cfg;
        _monitors = monitors;
        // Migrate the old boolean "usage from C0" into the new 4-way bar mode.
        if (_cfg.CoreUsageC0 && _cfg.CoreBarMode == 0) { _cfg.CoreBarMode = 1; _cfg.CoreUsageC0 = false; }
        // Migrate the old boolean CPU-per-core-graph into the new 3-way graph mode.
        if (_cfg.CpuPerCoreGraph && _cfg.CpuGraphMode == 0) _cfg.CpuGraphMode = 1;
        Theme.TooltipBg.Opacity = Math.Clamp(_cfg.TooltipOpacity, 0.3, 1.0);   // shared by all tooltips

        Title = "SidebarMonitor";
        Background = Theme.Page;

        _cpuPct = Theme.Text("", 16, Theme.InkPrimary, mono: true);
        _cpuPct.FontWeight = FontWeights.Bold;
        _status = Theme.Text(Loc.T("esperando al agente…"), 10, Theme.InkMuted);
        _minButton = Theme.Text("»", 11, Theme.InkMuted);
        _tabArrow = Theme.Text("‹", 12, Theme.InkMuted);
        _topRows = new (TextBlock, TextBlock, TextBlock)[8];

        _debug.Visibility = _cfg.LogVerbose ? Visibility.Visible : Visibility.Collapsed;
        _titleBar = BuildTitleBar();   // hosts _debug so it survives ApplySectionOrder's rebuild
        _stack.Children.Add(_titleBar);
        if (_cfg.LogCsv) _csv.Start(DateTime.Now);
        Register(new Section("cpu", Loc.T("CPU"), BuildCpu()));
        Register(new Section("ram", Loc.T("MEMORIA"), BuildRam()));
        Register(new Section("gpu", Loc.T("GPU"), BuildGpu()));
        Register(new Section("game", Loc.T("JUEGO"), _gameSection));
        Register(new Section("net", Loc.T("RED"), BuildNet()));
        Register(new Section("disk", Loc.T("DISCOS"), BuildDisks()));
        Register(new Section("top", Loc.T("PROCESOS"), BuildTop()));
        Register(new Section("docker", Loc.T("DOCKER"), BuildGuest(_dockerRows)));
        Register(new Section("wsl", Loc.T("WSL"), BuildGuest(_wslRows)));
        // Docker/WSL are opt-in: hidden by default so we never spawn docker/wsl unless asked.
        // A saved state (LoadSections) overrides this.
        Find("docker").Visibility = Visibility.Collapsed;
        Find("wsl").Visibility = Visibility.Collapsed;
        ApplySectionOrder();

        _body = new ScrollViewer
        {
            Content = _stack,
            Background = Theme.Page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };

        _tabArrow.HorizontalAlignment = HorizontalAlignment.Center;
        _tabArrow.VerticalAlignment = VerticalAlignment.Center;
        _tab = new Border { Background = Theme.Page, Child = _tabArrow, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };
        _tab.MouseLeftButtonUp += (_, _) => SetMinimized(false);
        ToolTipService.SetToolTip(_tab, Loc.T("Abrir SidebarMonitor"));

        var root = new Grid();
        root.Children.Add(_body);
        root.Children.Add(_tab);
        Content = root;

        ContextMenu = BuildMenu();
        LoadSections();

        ConfigureSparklineHovers();

        ApplyRefreshRates();
        Resized += OnPanelResized;
        _displaySettle.Tick += (_, _) => RecoverFromDisplayChange();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            ReapplyPlacement();
            SetClickThrough(_cfg.ClickThrough);
            SetMinimized(_cfg.Minimized);
            Tick();
            _timer.Start();

            // One delayed re-placement: if the shell wasn't ready at Loaded (explorer-spawned at
            // logon), the first AppBar registration can fall back to a plain edge placement. Retrying
            // once the message pump has settled registers the AppBar properly and reserves the strip.
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            settle.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); ReapplyPlacement(); };
            settle.Start();

            // Static RAM module info (model/type/speed) via WMI — slow first call, so off-thread.
            RamInfo.LoadAsync(() => Dispatcher.BeginInvoke(ApplyRamModulesMode));
        };
    }

    /// <summary>Show the WMI RAM module info per the configured mode: hidden, compact summary, or the
    /// full per-stick detail as text. The tooltip always carries the detail.</summary>
    private void ApplyRamModulesMode()
    {
        if (!RamInfo.HasData || _cfg.RamModulesMode == 0) { _ramModules.Visibility = Visibility.Collapsed; return; }
        // Module capacities are always binary GiB (their real size); the units toggle is for the
        // OS usage line only.
        string detail = RamInfo.DetailText();
        _ramModules.Text = _cfg.RamModulesMode == 2 && detail.Length > 0 ? detail : RamInfo.SummaryText();
        if (_ramModules.ToolTip is ToolTip tt && detail.Length > 0)
        {
            var tb = Theme.TipBlock();
            tb.Inlines.Add(Theme.TipHead(Loc.T("Módulos de memoria")));
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(detail));
            tt.Content = tb;
        }
        _ramModules.Visibility = Visibility.Visible;
    }

    public void AttachTray(TrayIcon tray)
    {
        _tray = tray;
        tray.ToggleRequested += () => Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        tray.ConfigRequested += () => { if (ContextMenu is not null) ContextMenu.IsOpen = true; };
        tray.ExitRequested += Close;
        tray.UpdateRequested += () => ApplyUpdate();
    }

    private void Register(Section s)
    {
        s.StateChanged += SaveSections;
        _sections.Add(s);
        _byKey[s.Key] = s;
    }

    /// <summary>The sections in display order: those named in the saved order first, then any
    /// unlisted ones in their default (registration) position.</summary>
    private IEnumerable<Section> OrderedSections()
    {
        var byKey = _sections.ToDictionary(s => s.Key);
        var seen = new HashSet<string>();
        foreach (var key in _cfg.SectionOrder)
            if (byKey.TryGetValue(key, out var s) && seen.Add(key))
                yield return s;
        foreach (var s in _sections)
            if (seen.Add(s.Key))
                yield return s;
    }

    /// <summary>Rehomes the section panels under the title bar in the configured order.</summary>
    private void ApplySectionOrder()
    {
        for (int i = _stack.Children.Count - 1; i >= 1; i--)   // keep the title bar at index 0
            _stack.Children.RemoveAt(i);
        foreach (var s in OrderedSections())
            _stack.Children.Add(s);
    }

    /// <summary>Moves a section one slot up (-1) or down (+1) and persists the new order.</summary>
    private void MoveSection(string key, int dir)
    {
        var order = OrderedSections().Select(s => s.Key).ToList();
        int i = order.IndexOf(key);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _cfg.SectionOrder = order;
        _cfg.Save();
        ApplySectionOrder();
        ContextMenu = BuildMenu();   // rebuild so the "Orden" submenu reflects the new positions
    }

    // ---------------------------------------------------------------- data

    private void Tick()
    {
        if (_reader is null)
        {
            _reader = SnapshotChannel.TryOpenReader(out string? err);
            if (_reader is null)
            {
                TryLaunchAgent();
                _status.Text = err is not null && err.Contains("versi", StringComparison.OrdinalIgnoreCase)
                    ? Loc.T("agente desfasado")
                    : Loc.T("esperando al agente…");
                return;
            }
        }

        if (!_reader.TryRead(out var s)) return;

        // Kept for the sensors-diagnostics report (Settings → Diagnóstico); one struct copy per tick.
        _lastSnap = s;
        _haveSnap = true;

        if (TestFakeFps)   // --fake-fps: sample data to preview the GAME section without a real game
        {
            NameField.Set(ref s.Frame.App, "Cyberpunk2077.exe");
            float wobble = (float)(12 * Math.Sin(Environment.TickCount64 / 1500.0));
            s.Frame.FpsPresented = 60 + wobble / 2;
            s.Frame.FpsDisplayed = 118 + wobble;   // frame generation active
            s.Frame.FrametimeMs = 1000f / s.Frame.FpsPresented;
            s.Frame.Low1PctFps = 47; s.Frame.Low01PctFps = 38;
            s.Frame.GpuBusyPct = 97; s.Frame.AnimationErrorMs = 1.4f; s.Frame.LatencyMs = 32;
        }

        // A dead agent leaves a stale map behind; detect it by the timestamp going flat.
        var age = DateTime.UtcNow - new DateTime(s.TimestampUtcTicks, DateTimeKind.Utc);
        if (age > TimeSpan.FromSeconds(10))
        {
            _status.Text = Loc.T("agente parado ({0:F0} s)", age.TotalSeconds);
            // The agent crashed leaving its map frozen. Once it's actually gone, drop the stale
            // reader and relaunch so we rebind to the new agent's map instead of reading a dead one
            // forever. TryLaunchAgent self-throttles via backoff, so this can't spawn a loop.
            if (_ownedAgent is null or { HasExited: true })
            {
                _reader?.Dispose();
                _reader = null;
                TryLaunchAgent();
            }
            return;
        }
        _agentLaunchStreak = 0;   // fresh data is flowing — clear the relaunch backoff

        // The elevated helper carries CPU temp/power (AMD SDK), SATA temps and per-process
        // attribution. When it is up we say nothing; when it is down, that is the only thing worth
        // flagging — everything else the unelevated agent covers on its own.
        _status.Text = s.CpuFromAmd ? ""
                     : !s.EtwAvailable ? Loc.T("sin helper (lanza SidebarMonitor.Etw)")
                     : "";

        // Diagnostics run regardless of minimised state: CSV keeps recording while tucked away, and
        // the verbose readout reflects live contract/SDK/helper state.
        if (_csv.IsRunning) _csv.Log(ref s, DateTime.UtcNow);
        if (_cfg.LogVerbose) UpdateDebugOverlay(ref s, age);

        // A minimized panel shows nothing; skip every update but keep the reader warm.
        if (_cfg.Minimized) return;

        var ci = CultureInfo.InvariantCulture;
        ref var c = ref s.Cpu;

        // Spread the core palette across THIS machine's core count so every preset uses its full
        // colour range (an 8-core rainbow spans the whole wheel, not just red→green). On the rare
        // change (first snapshot, or a topology change) reset the pen-caching widgets, like a palette
        // switch does.
        if (Theme.SetCoreCount(c.CoreCount)) { _cpuCoreSpark.ResetColors(); _cpuCoreGrid.ResetColors(); }

        // CPU model name, where the user asked for it: after the title, inside the section, or off.
        string cpuName = NameField.Get(ref c.Name);
        Find("cpu").SetTitleSuffix(_cfg.CpuNameMode == 1 ? ShortCpuName(cpuName) : "");
        if (_cfg.CpuNameMode == 2 && cpuName.Length > 0)
        {
            _cpuName.Text = cpuName;
            _cpuName.Visibility = Visibility.Visible;
        }
        else _cpuName.Visibility = Visibility.Collapsed;

        float freq = _cfg.CpuFreqMode switch { 1 => c.FreqMeanMhz, 2 => c.FreqMedianMhz, _ => c.FreqBestMhz };
        string ghz = float.IsNaN(freq) ? "" : string.Create(ci, $" {freq / 1000:F2}GHz");
        Find("cpu").SetSummary(string.Create(ci, $"{c.TotalUsagePct,3:F0}%{ghz}{W(c.PackagePowerW)}"));
        // Stays live while folded too (like GPU): the graph keeps accumulating so it's already
        // populated when opened. Skipped only when hidden outright or not due this tick.
        if (SectionDue("cpu") && Find("cpu").Visibility == Visibility.Visible)
        {
            _cpuPct.Text = string.Create(ci, $"{c.TotalUsagePct:F0} %");
            _cpuFreq.Text = float.IsNaN(freq) ? "—" : string.Create(ci, $"{freq / 1000:F2}");
            _cpuWatts.Text = float.IsNaN(c.PackagePowerW) ? "—" : string.Create(ci, $"{c.PackagePowerW:F1}");
            _cpuTemp.Text = float.IsNaN(c.TempC) ? "—" : string.Create(ci, $"{c.TempC:F1}");
            // Fan tile. HP WMI models (Victus/OMEN) carry real rpm, shown as "47% · 2700 rpm" (% is
            // the readable-at-a-glance value, rpm the detail). EC-duty models show just "%" under the
            // "%vent" caption. "—" when there's no source.
            if (!float.IsNaN(c.FanRpm))
            {
                _cpuFan.Text = float.IsNaN(c.FanPct) ? string.Create(ci, $"{c.FanRpm:F0}") : string.Create(ci, $"{c.FanPct:F0}%");
                _cpuFanCap.Text = string.Create(ci, $"· {c.FanRpm:F0} rpm");
            }
            else
            {
                _cpuFan.Text = float.IsNaN(c.FanPct) ? "—" : string.Create(ci, $"{c.FanPct:F0}");
                _cpuFanCap.Text = "%vent";
            }
            // Colour the temperature by how close it is to the SDK's throttle limit (Tjmax/cHTC),
            // and pulse red once it's within a few degrees — you can't miss it about to throttle.
            int tlvl = TempLevel(c.TempC, c.TjMaxC);
            _cpuTemp.Foreground = tlvl == 2 ? Theme.StatusCritical : tlvl == 1 ? Theme.StatusSerious : Theme.InkPrimary;
            SetTempBlink(tlvl == 2);

            // The binding-limiter indicator: which cap (power / current / thermal) is pulling the
            // boost back right now — the "is something throttling?" answer at a glance.
            if (_cfg.ShowThrottle && c.TjMaxC > 0)
            {
                var (ttext, tlevel) = ThrottleState(ref c);
                _cpuThrottle.Text = ttext;
                _cpuThrottle.Foreground = tlevel == 2 ? Theme.StatusCritical : tlevel == 1 ? Theme.StatusSerious : Theme.InkMuted;
                _cpuThrottle.Visibility = Visibility.Visible;
            }
            else _cpuThrottle.Visibility = Visibility.Collapsed;

            // Achieved best-core boost vs its session peak: the temperature-driven boost curve made
            // visible. On a Zen 4 X3D the clock eases down continuously with heat, so the best core
            // sits below its cool-idle peak (e.g. 4.78 vs 5.05) long before any hard cap — this shows
            // exactly that, with no driver. The peak stands in for the rated Fmax the SDK won't hand
            // us dynamically; the true dynamic limit lives in the SMU PM_Table (ring0 only).
            if (_cfg.ShowBoost && !float.IsNaN(c.FreqBestMhz) && c.FreqBestMhz > 0)
            {
                _bestFreqPeakMhz = Math.Max(_bestFreqPeakMhz, c.FreqBestMhz);
                // The SMU's live global limit (PawnIO PM_Table) is the real ceiling the peak was
                // approximating — when it's flowing, it takes over as the denominator.
                double cur = c.FreqBestMhz / 1000.0;
                double peak = c.LimitMhz > 0 ? c.LimitMhz / 1000.0 : _bestFreqPeakMhz / 1000.0;
                // "(mejor núcleo)" is an AMD-only label: the SDK gives a specific favored core's clock,
                // and CPPC preferred cores are real on every modern Ryzen. On Intel we show the real
                // boost value (fastest core via APERF/MPERF) but drop the label — a favored core is a
                // Turbo-Boost-Max-3.0 concept that doesn't apply to most/older Intel parts.
                string bestLabel = c.LimitMhz > 0 ? Loc.T(" (límite SMU)")
                                 : s.CpuFromAmd ? Loc.T(" (mejor núcleo)") : "";
                _cpuBoost.Text = string.Create(ci, $"boost {cur:F2} / {peak:F2} GHz{bestLabel}");
                _cpuBoost.Visibility = Visibility.Visible;
            }
            else _cpuBoost.Visibility = Visibility.Collapsed;

            // Optional AMD-SDK extras: VID, and the HWiNFO-style "Limits" line (frequency ceiling
            // + PPT/TDC/EDC/thermal usage), plus a thermal-throttle warning when it hits Tjmax.
            // Thermal uses die-avg ≥ Tjmax−3: validated under load, the average tracks the hotspot
            // (Tctl) within ~3°, so that's the real throttle point without needing the Tctl sensor.
            var extra = new List<string>(6);
            // Thermal throttle: Intel's real PROCHOT/thermal bit, or (AMD, or as a fallback) the
            // die temperature within 3° of Tjmax.
            bool throttle = (c.ThrottleFlags & 1) != 0
                            || (c.TjMaxC > 0 && !float.IsNaN(c.TempC) && c.TempC >= c.TjMaxC - 3f);
            if (_cfg.ShowCpuVid && c.VidV > 0) extra.Add(string.Create(ci, $"VID {c.VidV:F3}V"));
            if (_cfg.ShowCpuLimits)
            {
                if (throttle) extra.Add(Loc.T("⚠ throttle térmico"));
                if (c.PptPct > 0) extra.Add(string.Create(ci, $"PPT {c.PptPct:F0}%"));
                if (c.TdcPct > 0) extra.Add(string.Create(ci, $"TDC {c.TdcPct:F0}%"));
                if (c.EdcPct > 0) extra.Add(string.Create(ci, $"EDC {c.EdcPct:F0}%"));
                if (c.TjMaxC > 0 && !float.IsNaN(c.TempC)) extra.Add(Loc.T("térm {0:F0}%", c.TempC / c.TjMaxC * 100));
            }
            _cpuExtra.Text = string.Join("  ·  ", extra);
            _cpuExtra.Foreground = throttle && _cfg.ShowCpuLimits ? Theme.StatusCritical : Theme.InkMuted;
            _cpuExtra.Visibility = extra.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            switch (_cfg.CpuGraphMode)
            {
                case 1: _cpuCoreSpark.Push(ref s); break;
                case 2: _cpuCoreGrid.Push(ref s); break;
                default: _cpuSpark.Push(c.TotalUsagePct); break;
            }
            _coreRows.Update(ref s);
        }

        bool memBin = _cfg.MemUnitsBinary;
        string mu = Theme.MemUnit(memBin);
        double ramFrac = s.Mem.PhysTotal > 0 ? (double)s.Mem.PhysUsed / s.Mem.PhysTotal : 0;
        Find("ram").SetSummary(string.Create(ci, $"{ramFrac * 100,3:F0}%  {Theme.MemVal(s.Mem.PhysUsed, memBin)}{(memBin ? "G" : "g")}"));
        if (SectionDue("ram") && Find("ram").IsUpdateWorthy())
        {
            _ramText.Text = string.Create(ci, $"{Theme.MemVal(s.Mem.PhysUsed, memBin)} / {Theme.MemVal(s.Mem.PhysTotal, memBin)} {mu}");
            _ramMeter.Update(ramFrac);
            _commitText.Text = string.Create(ci, $"commit {Theme.MemVal(s.Mem.CommitUsed, memBin)} / {Theme.MemVal(s.Mem.CommitTotal, memBin)} {mu}");
        }

        // Primary GPU (Gpus[0]: discrete-first) model name after the GPU title, when asked. Inside
        // the section each GPU block already prints its own name, so mode 2 needs nothing here.
        Find("gpu").SetTitleSuffix(_cfg.GpuNameMode == 1 && s.GpuCount > 0
            ? ShortGpuName(NameField.Get(ref s.Gpus[0].Name)) : "");

        // The GPU section stays live even while folded (not just when expanded): its summary keeps
        // updating and its mini-graphs keep accumulating history, so opening it shows a populated
        // chart instead of an empty one that fills over the next few seconds. Only skip it when the
        // section is hidden outright, or when it isn't due this tick.
        if (s.GpuCount > 0 && SectionDue("gpu") && Find("gpu").Visibility == Visibility.Visible)
        {
            _gpuSection.Update(ref s);
            Find("gpu").SetSummary(_gpuSection.Summary);
        }

        // GAME/FPS: auto-appears only while a game is presenting (the helper's PresentMon fed data).
        bool gaming = _gameSection.Update(ref s);
        var gameSec = Find("game");
        gameSec.Visibility = gaming ? Visibility.Visible : Visibility.Collapsed;
        if (gaming)
        {
            gameSec.SetSummary(_gameSection.Summary);
            _gameSection.SecondsPerSample = EffectiveRefresh("game") / 1000.0;
        }

        // Only the primary interface — the one the default route uses. The rest (Tailscale,
        // the Hyper-V switches) are noise here. If none is flagged, fall back to the busiest.
        int prim = -1;
        for (int i = 0; i < s.NicCount; i++) if (s.Nics[i].IsPrimary) { prim = i; break; }
        if (prim < 0)
        {
            double best = -1;
            for (int i = 0; i < s.NicCount; i++)
            {
                double t = s.Nics[i].RxBytesPerSec + s.Nics[i].TxBytesPerSec;
                if (t > best) { best = t; prim = i; }
            }
        }

        double dl = prim >= 0 ? s.Nics[prim].RxBytesPerSec : 0;
        double ul = prim >= 0 ? s.Nics[prim].TxBytesPerSec : 0;
        bool netBin = _cfg.NetUnitsBinary;
        Find("net").SetSummary($"↓{Theme.BytesShort(dl, netBin)} ↑{Theme.BytesShort(ul, netBin)}");
        if (SectionDue("net") && Find("net").Visibility == Visibility.Visible)
        {
            _netSpark.Push((float)dl, (float)ul);
            _netDl.Text = Theme.Bytes(dl, netBin);
            _netUl.Text = Theme.Bytes(ul, netBin);

            if (prim >= 0)
            {
                ref var n = ref s.Nics[prim];
                string link = n.LinkBitsPerSec > 0 ? $" · {n.LinkBitsPerSec / 1_000_000:F0} Mbps" : "";
                _netPrimary.Text = $"{NameField.Get(ref n.Name)}{link}";
            }
            else _netPrimary.Text = Loc.T("sin interfaz activa");

            // Per-process breakdown from ETW; without the helper there is nothing to attribute.
            // The row count is fixed (padded with blank rows) so the sections below never shift as
            // the number of talking processes rises and falls — a steady glance, not a jitter.
            int netRows = _cfg.NetProcRows;
            SyncRows(_netProcRows, netRows);
            if (!s.EtwAvailable)
            {
                if (netRows > 0) ((TextBlock)_netProcRows.Children[0]).Text = Loc.T("· ETW para ver el tráfico por proceso");
                for (int i = 1; i < netRows; i++) ((TextBlock)_netProcRows.Children[i]).Text = " ";
            }
            else
            {
                for (int i = 0; i < netRows; i++)
                {
                    if (i < s.NetProcCount)
                    {
                        ref var np = ref s.NetProcs[i];
                        ((TextBlock)_netProcRows.Children[i]).Text =
                            $"{Truncate(NameField.Get(ref np.Name), 15),-15} ↓{Theme.BytesShort(np.RxBytesPerSec, netBin),-7} ↑{Theme.BytesShort(np.TxBytesPerSec, netBin)}";
                    }
                    else
                    {
                        ((TextBlock)_netProcRows.Children[i]).Text = " ";   // blank keeps the line height
                    }
                }
            }
        }

        var visibleDisks = VisibleDisks(ref s);
        double rd = 0, wr = 0, busiest = 0;
        foreach (int di in visibleDisks)
        {
            rd += s.Disks[di].ReadBytesPerSec;
            wr += s.Disks[di].WriteBytesPerSec;
            if (!float.IsNaN(s.Disks[di].ActivePct)) busiest = Math.Max(busiest, s.Disks[di].ActivePct);
        }
        bool diskBin = _cfg.DiskUnitsBinary;
        Find("disk").SetSummary(string.Create(ci, $"{busiest,3:F0}%  R{Theme.BytesShort(rd, diskBin)} W{Theme.BytesShort(wr, diskBin)}"));
        if (SectionDue("disk") && Find("disk").Visibility == Visibility.Visible)
        {
            SyncDiskBlocks(ref s, visibleDisks);
            for (int i = 0; i < visibleDisks.Count && i < _diskBlocks.Count; i++)
            {
                ref var d = ref s.Disks[visibleDisks[i]];
                var b = _diskBlocks[i];

                string label = NameField.Get(ref d.Label);
                string model = NameField.Get(ref d.Model);
                string vols = NameField.Get(ref d.Volumes);

                // A single labelled partition would show the label twice — as the title and again
                // in the volumes line. Merge: the head becomes the richer line (label + letter +
                // used/total), and the separate volumes row disappears. Several partitions (or an
                // unlabelled one) aren't redundant: head is the model, volumes lists the rest.
                if (d.VolumeCount == 1 && label.Length > 0)
                {
                    b.Head.Text = vols.Length > 0 ? vols : label;
                    b.Volumes.Text = "";
                    b.Volumes.Visibility = Visibility.Collapsed;
                }
                else
                {
                    b.Head.Text = model.Length > 0 ? model : label.Length > 0 ? label : NameField.Get(ref d.Name);
                    b.Volumes.Text = vols;
                    b.Volumes.Visibility = vols.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                b.Temp.Text = float.IsNaN(d.TempC) ? "" : string.Create(ci, $"{d.TempC:F0} °C");
                // A spinning disk sits happily at 45-50 °C; alarming there would cry wolf.
                b.Temp.Foreground = d.TempC >= 60 ? Theme.StatusCritical
                                  : d.TempC >= 52 ? Theme.StatusSerious
                                  : Theme.InkSecondary;

                string media = d.Media switch { DiskMedia.Ssd => "SSD", DiskMedia.Hdd => "HDD", _ => "" };
                string size = d.SizeBytes > 0 ? string.Create(ci, $"{d.SizeBytes / 1e12:F1} TB") : "";
                b.Sub.Text = string.Join("  ·  ", new[] { media, NameField.Get(ref d.Bus), size }.Where(x => x.Length > 0));

                float active = float.IsNaN(d.ActivePct) ? 0 : d.ActivePct;
                b.ActiveText.Text = Loc.T("actividad {0:F0} %", active);
                b.Active.Update(active / 100.0);

                b.Spark.Push((float)d.ReadBytesPerSec, (float)d.WriteBytesPerSec);
                b.Rates.Text = Loc.T("R {0,-6} W {1,-6} cola {2:F2}",
                    Theme.BytesShort(d.ReadBytesPerSec, diskBin), Theme.BytesShort(d.WriteBytesPerSec, diskBin), d.QueueLength);
            }
        }

        Find("top").SetSummary(TopSummary(ref s, ci));
        if (SectionDue("top") && Find("top").IsUpdateWorthy())
        {
            int rows = Math.Min(_topRows.Length, s.ProcCount);
            for (int i = 0; i < _topRows.Length; i++)
            {
                if (i >= rows) { _topRows[i].Name.Text = _topRows[i].Cpu.Text = _topRows[i].Mem.Text = ""; continue; }
                ref var p = ref s.Procs[i];
                string name = NameField.Get(ref p.Name);
                // "chrome ×42" says at a glance that this row is 42 processes summed.
                _topRows[i].Name.Text = p.Instances > 1 ? $"{name} ×{p.Instances}" : name;
                _topRows[i].Cpu.Text = string.Create(ci, $"{p.CpuPct:F1}%");
                _topRows[i].Mem.Text = string.Create(ci, $"{p.WorkingSet / (1024.0 * 1024):F0} MB");
            }
            _totals.Text = Loc.T("{0} procesos · {1} threads", s.TotalProcesses, s.TotalThreads);
        }

        if (SectionDue("docker") && Find("docker").IsUpdateWorthy()) UpdateGuest(_docker, _dockerRows, "docker", Loc.T("cont."));
        if (SectionDue("wsl") && Find("wsl").IsUpdateWorthy()) UpdateGuest(_wsl, _wslRows, "wsl", Loc.T("proc."));
    }

    /// <summary>The heaviest process, for the folded header.</summary>
    private static string TopSummary(ref Snapshot s, CultureInfo ci)
    {
        if (s.ProcCount == 0) return "";
        ref var first = ref s.Procs[0];
        string name = NameField.Get(ref first.Name);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.Create(ci, $"{Truncate(name, 12)} {first.CpuPct:F1}%");
    }

    private static string W(float watts) => float.IsNaN(watts) ? "" : string.Create(CultureInfo.InvariantCulture, $" {watts:F0}W");

    /// <summary>CPU temperature severity by nearness to the throttle limit. With the SDK's Tjmax
    /// (cHTC) known, 1 = amber within 12 °C, 2 = red within 4 °C of it; otherwise generic 80/90 °C.</summary>
    private static int TempLevel(float temp, float tjmax)
    {
        if (float.IsNaN(temp)) return 0;
        float hot = tjmax > 0 ? tjmax - Theme.TempHotMarginC : Theme.TempHotC;
        float warm = tjmax > 0 ? tjmax - Theme.TempWarmMarginC : Theme.TempWarmC;
        return temp >= hot ? 2 : temp >= warm ? 1 : 0;
    }

    /// <summary>
    /// Which HARD cap is throttling the boost right now, from the AMD SDK: power (PPT), current
    /// (TDC/EDC) or thermal. Level 2 = actively throttled at a cap, 1 = approaching one, 0 = no hard
    /// cap. Note "sin throttle" is NOT "max boost": on a Zen 4 X3D the achievable clock is a
    /// continuous function of temperature that eases down well before any hard cap, so this only
    /// answers "is a hard limit binding", never "is the chip boosting freely". The thermal test uses
    /// die-avg ≥ Tjmax−3: empirically (idle→scalar→AVX2→AVX-512 against HWiNFO's Tctl) the die
    /// average tracks the Tctl hotspot within ~3° under load — exactly when temps near Tjmax — so
    /// that threshold marks the real throttle point without the Tctl sensor we can't read.
    /// </summary>
    private static (string Text, int Level) ThrottleState(ref SidebarMonitor.Shared.CpuInfo c)
    {
        var hard = new List<string>(3);
        var warn = new List<string>(3);
        bool haveTemp = c.TjMaxC > 0 && !float.IsNaN(c.TempC);

        // ThrottleFlags carries Intel's *authoritative* active-cap bits (from IA32_THERM_STATUS); AMD
        // leaves them 0 and the cap is inferred from the PPT/TDC/EDC percentages. Either route counts
        // as a hard throttle.
        bool flagThermal = (c.ThrottleFlags & 1) != 0;
        bool flagPower   = (c.ThrottleFlags & 2) != 0;
        bool flagCurrent = (c.ThrottleFlags & 4) != 0;

        if (flagPower || c.PptPct >= 99) hard.Add(Loc.T("POT")); else if (c.PptPct >= 95) warn.Add(Loc.T("POT"));
        if (flagCurrent || c.TdcPct >= 99 || c.EdcPct >= 99) hard.Add(Loc.T("CORR")); else if (c.TdcPct >= 95 || c.EdcPct >= 95) warn.Add(Loc.T("CORR"));
        if (flagThermal || (haveTemp && c.TempC >= c.TjMaxC - 3)) hard.Add(Loc.T("TÉRM")); else if (haveTemp && c.TempC >= c.TjMaxC - 7) warn.Add(Loc.T("TÉRM"));

        if (hard.Count > 0) return (Loc.T("throttle: {0}", string.Join("+", hard)), 2);
        if (warn.Count > 0) return (Loc.T("cerca de throttle: {0}", string.Join("+", warn)), 1);
        return (Loc.T("sin throttle"), 0);
    }

    private bool _tempBlink;

    /// <summary>Pulses the CPU temperature's opacity while it's critical (near Tjmax), and stops
    /// cleanly when it drops back.</summary>
    private void SetTempBlink(bool on)
    {
        if (on == _tempBlink) return;
        _tempBlink = on;
        if (on)
        {
            var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(450))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            _cpuTemp.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            _cpuTemp.BeginAnimation(OpacityProperty, null);
            _cpuTemp.Opacity = 1.0;
        }
    }

    private Section Find(string key) => _byKey[key];

    private static void SyncRows(StackPanel host, int count)
    {
        while (host.Children.Count < count) host.Children.Add(Muted());
        while (host.Children.Count > count) host.Children.RemoveAt(host.Children.Count - 1);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void TryLaunchAgent()
    {
        if (_ownedAgent is { HasExited: false }) return;
        if (DateTime.UtcNow < _nextAgentLaunch) return;   // still backing off from the last attempt
        string path = Path.Combine(AppContext.BaseDirectory, "SidebarMonitor.Agent.exe");
        if (!File.Exists(path)) return;

        // Back off after each launch so an agent that crashes on startup can't spawn a process
        // every tick. Grows 2,4,8,16,30s and is reset to zero once healthy data flows again.
        _agentLaunchStreak = Math.Min(_agentLaunchStreak + 1, 5);
        _nextAgentLaunch = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(30, 2 << (_agentLaunchStreak - 1)));

        try
        {
            // GPU vendor sensors sample every Nth tick, where N = GpuRefreshMs / RefreshMs (≥1). This
            // is the knob that keeps an idle dGPU from being woken every second (see UiConfig.GpuRefreshMs).
            int gpuEvery = Math.Max(1, (int)Math.Round(_cfg.GpuRefreshMs / (double)Math.Max(1, _cfg.RefreshMs)));
            _ownedAgent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                Arguments = $"--interval={_cfg.RefreshMs} --gpu-every={gpuEvery}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { /* the status line keeps saying "esperando" */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        SaveSections();
        _cfg.Save();
        _csv.Dispose();   // flush and close any open CSV
        if (_ownedAgent is { HasExited: false }) { try { _ownedAgent.Kill(); } catch { } }
        _reader?.Dispose();
        _tray?.Dispose();
        base.OnClosing(e);
        Application.Current?.Shutdown();
    }

}

internal static class SectionExtensions
{
    /// <summary>Folded or hidden sections skip their body updates; the header summary is enough.</summary>
    public static bool IsUpdateWorthy(this Section s) => s.Visibility == Visibility.Visible && s.Expanded;
}
