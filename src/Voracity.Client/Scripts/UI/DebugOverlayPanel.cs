using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

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
/// Interactive Debug Mode overlay backed by Scenes/UI/DebugOverlay.tscn.
/// </summary>
public sealed partial class DebugOverlayPanel : PanelContainer
{
    [Export] public Label TitleLabel { get; set; } = null!;
    [Export] public Button HideButton { get; set; } = null!;
    [Export] public Label PerfLabel { get; set; } = null!;
    [Export] public Label SimLabel { get; set; } = null!;
    [Export] public Label ProvLabel { get; set; } = null!;
    [Export] public Button Step1MButton { get; set; } = null!;
    [Export] public Button Step1YButton { get; set; } = null!;
    [Export] public Button Step5YButton { get; set; } = null!;
    [Export] public Button BenchButton { get; set; } = null!;
    [Export] public Button AuditButton { get; set; } = null!;
    [Export] public Label ReportLabel { get; set; } = null!;

    private LocalizationCatalog? _loc;
    private GameSettings? _settings;
    private SimTelemetryMetrics _lastTelemetry;
    private RenderSnapshot? _lastSnapshot;
    private SimulationIntegrityReport? _lastIntegrity;
    private ProvinceSnapshot? _focusProvince;
    private string _lastLogMessage = string.Empty;

    public event Action<int>? FastForwardDaysRequested;
    public event Action? BenchmarkSpeedsRequested;
    public event Action? AuditWorldRequested;
    public event Action? CollapseRequested;

    public void Initialize(LocalizationCatalog loc, GameSettings settings)
    {
        _loc = loc;
        _settings = settings;
        Visible = false;
        if (IsNodeReady())
            ApplySettings(settings);
    }

    public override void _Ready()
    {
        Visible = false;
        TitleLabel ??= GetNode<Label>("VBox/Header/TitleLabel");
        HideButton ??= GetNode<Button>("VBox/Header/HideBtn");
        PerfLabel ??= GetNode<Label>("VBox/PerfLabel");
        SimLabel ??= GetNode<Label>("VBox/SimLabel");
        ProvLabel ??= GetNode<Label>("VBox/ProvLabel");
        Step1MButton ??= GetNode<Button>("VBox/Actions/Step1MBtn");
        Step1YButton ??= GetNode<Button>("VBox/Actions/Step1YBtn");
        Step5YButton ??= GetNode<Button>("VBox/Actions/Step5YBtn");
        BenchButton ??= GetNode<Button>("VBox/Actions/BenchBtn");
        AuditButton ??= GetNode<Button>("VBox/Actions/AuditBtn");
        ReportLabel ??= GetNode<Label>("VBox/Scroll/ReportLabel");

        HideButton.Pressed += () => CollapseRequested?.Invoke();
        Step1MButton.Pressed += () => FastForwardDaysRequested?.Invoke(30);
        Step1YButton.Pressed += () => FastForwardDaysRequested?.Invoke(360);
        Step5YButton.Pressed += () => FastForwardDaysRequested?.Invoke(1800);
        BenchButton.Pressed += () => BenchmarkSpeedsRequested?.Invoke();
        AuditButton.Pressed += () => AuditWorldRequested?.Invoke();

        if (_loc is not null && _settings is not null)
            ApplySettings(_settings);
    }

    public void ApplySettings(GameSettings settings)
    {
        _settings = settings;
        if (_loc is null || TitleLabel is null)
            return;

        var l = settings.ResolveActiveLanguage();
        TitleLabel.Text = _loc.Get(l, "debug.title");
        HideButton.Text = _loc.Get(l, "debug.hide");
        Step1MButton.Text = _loc.Get(l, "debug.step_month");
        Step1YButton.Text = _loc.Get(l, "debug.step_year");
        Step5YButton.Text = _loc.Get(l, "debug.step_5y");
        BenchButton.Text = _loc.Get(l, "debug.bench_speeds");
        AuditButton.Text = _loc.Get(l, "debug.audit");
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

        RedrawLabels();
    }

    public void SetReportText(string text)
    {
        _lastLogMessage = text;
        if (ReportLabel is not null)
            ReportLabel.Text = text;
    }

    private void RedrawLabels()
    {
        if (_settings is null || PerfLabel is null)
            return;

        var pt = _settings.ResolveActiveLanguage().StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var m = _lastTelemetry;

        if (pt)
        {
            var status = m.IsPaused ? "PAUSA" : $"VEL {m.Speed}x";
            PerfLabel.Text =
                $"FPS: {m.Fps:F0} ({m.FrameMs:F1} ms)  |  {status}  |  Meta: {m.TargetSubTicksPerSec:F1} t/s · Real: {m.MeasuredSubTicksPerSec:F1} t/s\n" +
                $"Tick: últ {m.LastStepMs:F2} ms · méd {m.AvgSubTickMs:F3} ms/tick · pico {m.MaxStepMs:F2} ms  |  UI Snap/s: {m.SnapshotPushesPerSec}  |  RAM: {m.ManagedMemoryMb:F1} MB (GC {m.GcGen0}/{m.GcGen1}/{m.GcGen2})";
        }
        else
        {
            var status = m.IsPaused ? "PAUSED" : $"SPD {m.Speed}x";
            PerfLabel.Text =
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

            SimLabel.Text = pt
                ? $"Data: {s.Date.ToDisplayString()}  |  Ticks: {totalTicks:N0}  |  Pops Ativos: {s.ActivePopCount}  |  Pop Mundial: {s.TotalWorldPopulation:N0}  |  Cálculos: {healthTxt}"
                : $"Date: {s.Date.ToDisplayString()}  |  Ticks: {totalTicks:N0}  |  Active Pops: {s.ActivePopCount}  |  World Pop: {s.TotalWorldPopulation:N0}  |  Math: {healthTxt}";
        }

        if (_focusProvince is not null)
        {
            var p = _focusProvince;
            ProvLabel.Text = pt
                ? $"Foco: {p.Name} ({p.ProvinceId} #{p.NumericalIndex}) [{p.Kind}]  |  Área: {p.StateId} [{p.OwnerNationId}]  |  {p.Rank} · Ctrl {p.Control:F0}% (Prox {p.Proximity:F0}%) · RGO Lv.{p.RgoLevel}"
                : $"Focus: {p.Name} ({p.ProvinceId} #{p.NumericalIndex}) [{p.Kind}]  |  Area: {p.StateId} [{p.OwnerNationId}]  |  {p.Rank} · Ctrl {p.Control:F0}% (Prox {p.Proximity:F0}%) · RGO Lv.{p.RgoLevel}";
        }
        else
        {
            ProvLabel.Text = pt
                ? "Foco: passe o mouse ou clique em uma província para inspecionar os dados brutos."
                : "Focus: hover or click a province to inspect raw simulation values.";
        }

        if (string.IsNullOrEmpty(_lastLogMessage))
        {
            ReportLabel.Text = pt
                ? "Pronto. Use '+5 Anos' para simular 1.800 dias ou 'Testar Vel. 1x–5x' para medir velocidade real."
                : "Ready. Use '+5 Years' to simulate 1,800 days or 'Bench Speeds 1x–5x' to measure throughput.";
        }
    }
}
