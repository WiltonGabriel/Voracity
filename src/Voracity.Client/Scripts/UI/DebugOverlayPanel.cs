using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

/// <summary>
/// Lightweight, read-only telemetry snapshot passed from the Godot frame loop to the Debug Overlay.
/// </summary>
public readonly record struct SimTelemetryMetrics(
    double Fps,
    double FrameMs,
    int Speed,
    bool IsPaused,
    double TargetSubTicksPerSec,
    double MeasuredSubTicksPerSec,
    double LastStepMs,
    double AvgSubTickMs,
    double MaxStepMs,
    int SnapshotPushesPerSec,
    double ManagedMemoryMb,
    int GcGen0,
    int GcGen1,
    int GcGen2);

/// <summary>
/// Interactive Debug Mode panel designed for both human developers/players and automated QA/AI runs.
/// Enabled via Settings ("Modo Debug") or --debug-mode; collapsed/expanded with F3.
/// </summary>
public sealed partial class DebugOverlayPanel : PanelContainer
{
    private readonly LocalizationCatalog _loc;
    private GameSettings _settings;

    private Label _titleLabel = null!;
    private Button _hideBtn = null!;
    private Label _perfLabel = null!;
    private Label _simLabel = null!;
    private Label _provLabel = null!;
    private Label _reportLabel = null!;

    private Button _btnStep1M = null!;
    private Button _btnStep1Y = null!;
    private Button _btnStep5Y = null!;
    private Button _btnBench = null!;
    private Button _btnAudit = null!;

    private SimTelemetryMetrics _lastTelemetry;
    private RenderSnapshot? _lastSnapshot;
    private SimulationIntegrityReport? _lastIntegrity;
    private ProvinceSnapshot? _focusProvince;
    private string _lastLogMessage = string.Empty;

    public event Action<int>? FastForwardDaysRequested;
    public event Action? BenchmarkSpeedsRequested;
    public event Action? AuditWorldRequested;
    public event Action? CollapseRequested;

    public DebugOverlayPanel(LocalizationCatalog loc, GameSettings settings)
    {
        _loc = loc;
        _settings = settings;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.CenterTop);
        OffsetLeft = -310;
        OffsetRight = 310;
        OffsetTop = 94;
        OffsetBottom = 352;
        MouseFilter = MouseFilterEnum.Stop;

        var style = UiThemeFactory.CreateBox(
            new Color(0.05f, 0.06f, 0.09f, 0.94f),
            UiThemeFactory.CyanAether,
            margin: 10);
        AddThemeStyleboxOverride("panel", style);

        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 6);
        AddChild(root);

        var hdr = new HBoxContainer();
        hdr.AddThemeConstantOverride("separation", 8);
        _titleLabel = UiThemeFactory.CreateHeader("DEBUG", 13, UiThemeFactory.CyanAether);
        _titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        hdr.AddChild(_titleLabel);

        _hideBtn = UiThemeFactory.CreateStyledButton("Hide", 72, 24, UiThemeFactory.SteelBorder);
        _hideBtn.Pressed += () => CollapseRequested?.Invoke();
        hdr.AddChild(_hideBtn);
        root.AddChild(hdr);

        _perfLabel = CreateMonoLabel(new Color(0.93f, 0.90f, 0.84f));
        _simLabel = CreateMonoLabel(UiThemeFactory.GoldAccent);
        _provLabel = CreateMonoLabel(UiThemeFactory.MutedText);
        root.AddChild(_perfLabel);
        root.AddChild(_simLabel);
        root.AddChild(_provLabel);

        var actions = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddThemeConstantOverride("h_separation", 6);
        actions.AddThemeConstantOverride("v_separation", 4);

        _btnStep1M = UiThemeFactory.CreateStyledButton("+1M", 0, 26, UiThemeFactory.BronzeAccent);
        _btnStep1M.Pressed += () => FastForwardDaysRequested?.Invoke(30);
        actions.AddChild(_btnStep1M);

        _btnStep1Y = UiThemeFactory.CreateStyledButton("+1Y", 0, 26, UiThemeFactory.BronzeAccent);
        _btnStep1Y.Pressed += () => FastForwardDaysRequested?.Invoke(360);
        actions.AddChild(_btnStep1Y);

        _btnStep5Y = UiThemeFactory.CreateStyledButton("+5Y", 0, 26, UiThemeFactory.GoldAccent);
        _btnStep5Y.Pressed += () => FastForwardDaysRequested?.Invoke(1800);
        actions.AddChild(_btnStep5Y);

        _btnBench = UiThemeFactory.CreateStyledButton("Bench", 0, 26, UiThemeFactory.CyanAether);
        _btnBench.Pressed += () => BenchmarkSpeedsRequested?.Invoke();
        actions.AddChild(_btnBench);

        _btnAudit = UiThemeFactory.CreateStyledButton("Audit", 0, 26, UiThemeFactory.EmeraldPositive);
        _btnAudit.Pressed += () => AuditWorldRequested?.Invoke();
        actions.AddChild(_btnAudit);
        root.AddChild(actions);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 74),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _reportLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = string.Empty
        };
        _reportLabel.AddThemeFontSizeOverride("font_size", 11);
        _reportLabel.AddThemeColorOverride("font_color", UiThemeFactory.CyanAether);
        scroll.AddChild(_reportLabel);
        root.AddChild(scroll);

        ApplySettings(_settings);
    }

    public void ApplySettings(GameSettings settings)
    {
        _settings = settings;
        var l = settings.ResolveActiveLanguage();
        _titleLabel.Text = _loc.Get(l, "debug.title");
        _hideBtn.Text = _loc.Get(l, "debug.hide");
        _btnStep1M.Text = _loc.Get(l, "debug.step_month");
        _btnStep1Y.Text = _loc.Get(l, "debug.step_year");
        _btnStep5Y.Text = _loc.Get(l, "debug.step_5y");
        _btnBench.Text = _loc.Get(l, "debug.bench_speeds");
        _btnAudit.Text = _loc.Get(l, "debug.audit");
        RedrawLabels();
    }

    public void SetFocusProvince(ProvinceSnapshot? focusProvince)
    {
        _focusProvince = focusProvince;
        RedrawLabels();
    }

    public void UpdateTelemetry(
        in SimTelemetryMetrics metrics,
        RenderSnapshot? snapshot,
        SimulationIntegrityReport? integrity,
        ProvinceSnapshot? focusProvince = null)
    {
        _lastTelemetry = metrics;
        if (snapshot is not null)
            _lastSnapshot = snapshot;
        if (integrity is not null)
            _lastIntegrity = integrity;
        if (focusProvince is not null)
            _focusProvince = focusProvince;

        if (Visible)
            RedrawLabels();
    }

    public void SetReportText(string text)
    {
        _lastLogMessage = text;
        if (_reportLabel is not null)
            _reportLabel.Text = text;
    }

    private void RedrawLabels()
    {
        var pt = _settings.ResolveActiveLanguage().StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var m = _lastTelemetry;

        if (pt)
        {
            var status = m.IsPaused ? "PAUSA" : $"VEL {m.Speed}x";
            _perfLabel.Text =
                $"FPS: {m.Fps:F0} ({m.FrameMs:F1} ms)  |  {status}  |  Meta: {m.TargetSubTicksPerSec:F1} t/s · Real: {m.MeasuredSubTicksPerSec:F1} t/s\n" +
                $"Tick: últ {m.LastStepMs:F2} ms · méd {m.AvgSubTickMs:F3} ms/tick · pico {m.MaxStepMs:F2} ms  |  UI Snap/s: {m.SnapshotPushesPerSec}  |  RAM: {m.ManagedMemoryMb:F1} MB (GC {m.GcGen0}/{m.GcGen1}/{m.GcGen2})";
        }
        else
        {
            var status = m.IsPaused ? "PAUSED" : $"SPD {m.Speed}x";
            _perfLabel.Text =
                $"FPS: {m.Fps:F0} ({m.FrameMs:F1} ms)  |  {status}  |  Target: {m.TargetSubTicksPerSec:F1} t/s · Actual: {m.MeasuredSubTicksPerSec:F1} t/s\n" +
                $"Tick: last {m.LastStepMs:F2} ms · avg {m.AvgSubTickMs:F3} ms/tick · peak {m.MaxStepMs:F2} ms  |  UI Snap/s: {m.SnapshotPushesPerSec}  |  RAM: {m.ManagedMemoryMb:F1} MB (GC {m.GcGen0}/{m.GcGen1}/{m.GcGen2})";
        }

        if (_lastSnapshot is not null)
        {
            var s = _lastSnapshot;
            var healthTxt = _lastIntegrity is null
                ? "..."
                : (_lastIntegrity.IsHealthy
                    ? (pt ? "Íntegro (0 erros)" : "Healthy (0 issues)")
                    : (pt ? $"ALERTA ({_lastIntegrity.Anomalies.Count})" : $"WARN ({_lastIntegrity.Anomalies.Count})"));
            var totalTicks = _lastIntegrity?.TotalTicksExecuted ?? ((s.Date.Year - 1380) * 1440 + (s.Date.Month - 1) * 120 + (s.Date.Day - 1) * 4 + s.Date.SubTick6h);

            _simLabel.Text = pt
                ? $"Data: {s.Date.ToDisplayString()}  |  Ticks: {totalTicks:N0}  |  Pops Ativos: {s.ActivePopCount}  |  Pop Mundial: {s.TotalWorldPopulation:N0}  |  Cálculos: {healthTxt}"
                : $"Date: {s.Date.ToDisplayString()}  |  Ticks: {totalTicks:N0}  |  Active Pops: {s.ActivePopCount}  |  World Pop: {s.TotalWorldPopulation:N0}  |  Math: {healthTxt}";
        }

        if (_focusProvince is not null)
        {
            var p = _focusProvince;
            _provLabel.Text = pt
                ? $"Foco: {p.Name} ({p.ProvinceId} #{p.NumericalIndex}) [{p.Kind}]  |  Área: {p.StateId} [{p.OwnerNationId}]  |  {p.Rank} · Ctrl {p.Control:F0}% (Prox {p.Proximity:F0}%) · RGO Lv.{p.RgoLevel}"
                : $"Focus: {p.Name} ({p.ProvinceId} #{p.NumericalIndex}) [{p.Kind}]  |  Area: {p.StateId} [{p.OwnerNationId}]  |  {p.Rank} · Ctrl {p.Control:F0}% (Prox {p.Proximity:F0}%) · RGO Lv.{p.RgoLevel}";
        }
        else
        {
            _provLabel.Text = pt
                ? "Foco: passe o mouse ou clique em uma província para inspecionar os dados brutos."
                : "Focus: hover or click a province to inspect raw simulation values.";
        }

        if (string.IsNullOrEmpty(_lastLogMessage))
        {
            _reportLabel.Text = pt
                ? "Pronto. Use '+5 Anos' para simular 1.800 dias ou 'Testar Vel. 1x–5x' para medir velocidade real."
                : "Ready. Use '+5 Years' to simulate 1,800 days or 'Bench Speeds 1x–5x' to measure throughput.";
        }
    }

    private static Label CreateMonoLabel(Color color)
    {
        var lbl = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }
}
