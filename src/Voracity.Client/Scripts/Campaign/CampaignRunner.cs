using System.Diagnostics;
using System.Text;
using Godot;
using Voracity.Client.UI;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Client.Campaign;

/// <summary>
/// Owns the active SimulationWorld, fixed-rate sub-tick accumulator, live telemetry sampling,
/// debug fast-forwarding, benchmarks, and EU5 integrity auditing.
/// </summary>
public sealed class CampaignRunner
{
    private double _tickAccumulatorSeconds;
    private double _telemetryWindowSeconds;
    private int _telemetryFramesInWindow;
    private int _telemetryTicksInWindow;
    private int _telemetrySnapshotsInWindow;
    private double _telemetryTotalTickMsInWindow;
    private int _telemetryStepsInWindow;
    private double _telemetryMaxStepMs;
    private double _lastStepMs;
    private double _secondsSinceLastSnapshotPush;

    public SimulationWorld? World { get; private set; }
    public SimTelemetryMetrics LatestTelemetry { get; private set; }
    public SimulationIntegrityReport? LatestIntegrity { get; private set; }

    public static double GetTargetSubTicksPerSecond(int speed) => speed switch
    {
        1 => 2.0,
        2 => 5.0,
        3 => 12.0,
        4 => 28.0,
        _ => 96.0
    };

    public void AttachWorld(SimulationWorld world)
    {
        World = world;
        _tickAccumulatorSeconds = 0.0;
        LatestIntegrity = world.AuditIntegrity();
    }

    public void ClearWorld()
    {
        World = null;
        _tickAccumulatorSeconds = 0.0;
    }

    public void ResetAccumulator() => _tickAccumulatorSeconds = 0.0;

    public void NotifySnapshotPushed()
    {
        _secondsSinceLastSnapshotPush = 0.0;
        _telemetrySnapshotsInWindow++;
        if (World is not null)
            LatestIntegrity = World.AuditIntegrity();
    }

    public (int ExecutedSteps, bool NeedFullSnapshot) StepFrame(double delta, bool pauseMenuOpen)
    {
        if (World is null)
            return (0, false);

        var targetRate = GetTargetSubTicksPerSecond(World.Speed);
        var executedNow = 0;
        var needFullSnapshot = false;
        var crossedMidnight = false;

        if (!World.IsPaused && !pauseMenuOpen)
        {
            _tickAccumulatorSeconds = Math.Min(_tickAccumulatorSeconds + delta * targetRate, targetRate * 0.5);
            var plannedSteps = (int)Math.Floor(_tickAccumulatorSeconds);
            if (plannedSteps > 0)
            {
                var swStart = Stopwatch.GetTimestamp();
                var maxSteps = Math.Min(plannedSteps, 64);
                for (var s = 0; s < maxSteps; s++)
                {
                    World.StepSubTicks(1);
                    executedNow++;

                    var d = World.CurrentDate;
                    if (d.SubTick6h == 0)
                    {
                        crossedMidnight = true;
                        if (d.Day % 7 == 0 || d.Day == 1)
                            needFullSnapshot = true;
                    }

                    if (executedNow >= 4 && Stopwatch.GetElapsedTime(swStart).TotalMilliseconds >= 10.0)
                        break;
                }

                _tickAccumulatorSeconds = Math.Max(0.0, _tickAccumulatorSeconds - executedNow);
                var stepMs = Stopwatch.GetElapsedTime(swStart).TotalMilliseconds;
                _lastStepMs = stepMs;
                _telemetryTicksInWindow += executedNow;
                _telemetryStepsInWindow += executedNow;
                _telemetryTotalTickMsInWindow += stepMs;
                if (stepMs > _telemetryMaxStepMs)
                    _telemetryMaxStepMs = stepMs;
            }
        }
        else
        {
            _tickAccumulatorSeconds = 0.0;
        }

        _secondsSinceLastSnapshotPush += delta;
        var pushFull = executedNow > 0 && (needFullSnapshot || (crossedMidnight && _secondsSinceLastSnapshotPush >= 0.25));
        UpdateTelemetry(delta, targetRate);
        return (executedNow, pushFull);
    }

    public string FormatFastForwardReport(int days)
    {
        if (World is null)
            return string.Empty;

        var startPop = World.AuditIntegrity().TotalWorldPopulation;
        var sw = Stopwatch.StartNew();
        World.StepDays(days);
        sw.Stop();

        var report = World.AuditIntegrity();
        LatestIntegrity = report;

        var perDayMs = sw.Elapsed.TotalMilliseconds / Math.Max(1, days);
        var popDeltaPct = startPop > 0 ? ((report.TotalWorldPopulation - startPop) * 100.0 / startPop) : 0.0;
        var treasuryGold = new MonetaryAmount(report.TotalTreasuryCoppers).ToGold();
        return
            $"+{days}d ({days * SimulationWorld.SubTicksPerDay} ticks) em {sw.Elapsed.TotalMilliseconds:F1} ms ({perDayMs:F3} ms/dia) | " +
            $"Data: {report.Date.ToDisplayString()} | Pop: {startPop:N0} -> {report.TotalWorldPopulation:N0} ({popDeltaPct:+0.00;-0.00;0.00}%) | " +
            $"Tesouro Global: {treasuryGold:N0} Ouro | Controle Médio: {report.AverageControl:F1}% | Fome: {report.StarvingProvincesCount} | " +
            $"Preços: [{report.MinPriceRatio:F2}x..{report.MaxPriceRatio:F2}x] | Integridade: {(report.IsHealthy ? "OK" : $"ERRO ({report.Anomalies.Count})")}";
    }

    public string FormatBenchmarkReport(WorldDatabase db)
    {
        var sw = Stopwatch.StartNew();
        var benchWorld = new SimulationWorld(db, World?.PlayerNationId ?? "AET");
        benchWorld.StepDays(360);
        sw.Stop();
        var engineTicksPerSec = (360 * SimulationWorld.SubTicksPerDay) / Math.Max(0.0001, sw.Elapsed.TotalSeconds);

        var sb = new StringBuilder();
        sb.Append($"Núcleo puro EU5 (1 ano / 1.440 ticks): {sw.Elapsed.TotalMilliseconds:F1} ms ({engineTicksPerSec:F0} ticks/s máx) | Escala real: ");
        for (var spd = 1; spd <= 5; spd++)
        {
            var tps = GetTargetSubTicksPerSecond(spd);
            var dps = tps / SimulationWorld.SubTicksPerDay;
            sb.Append($"{spd}x={tps:F0}t/s ({dps:F2}d/s)");
            if (spd < 5) sb.Append(" · ");
        }
        return sb.ToString();
    }

    public string FormatIntegrityAuditReport()
    {
        if (World is null)
            return string.Empty;

        var report = World.AuditIntegrity();
        LatestIntegrity = report;
        var treasuryGold = new MonetaryAmount(report.TotalTreasuryCoppers).ToGold();
        return report.IsHealthy
            ? $"Auditoria OK ({report.Date.ToDisplayString()}): {report.NationsCount} nações, {report.LocationsCount} locations, {report.MarketsCount} mercados, {report.ActivePopCount} pops ({report.TotalWorldPopulation:N0} hab), Tesouro {treasuryGold:N0} Ouro, Controle {report.AverageControl:F1}%, Fome: {report.StarvingProvincesCount}, Preços [{report.MinPriceRatio:F2}x..{report.MaxPriceRatio:F2}x]."
            : $"ANOMALIAS ({report.Anomalies.Count}): {string.Join(" | ", report.Anomalies.Take(4))}";
    }

    private void UpdateTelemetry(double delta, double targetRate)
    {
        _telemetryFramesInWindow++;
        _telemetryWindowSeconds += delta;

        if (_telemetryWindowSeconds >= 0.5)
        {
            var fps = _telemetryFramesInWindow / _telemetryWindowSeconds;
            var frameMs = 1000.0 / Math.Max(1.0, fps);
            var realTicksPerSec = _telemetryTicksInWindow / _telemetryWindowSeconds;
            var avgSubTickMs = _telemetryStepsInWindow > 0 ? _telemetryTotalTickMsInWindow / _telemetryStepsInWindow : 0.0;
            var snapPerSec = (int)Math.Round(_telemetrySnapshotsInWindow / _telemetryWindowSeconds);

            LatestTelemetry = new SimTelemetryMetrics(
                fps,
                frameMs,
                World?.Speed ?? 1,
                World?.IsPaused ?? true,
                targetRate,
                realTicksPerSec,
                _lastStepMs,
                avgSubTickMs,
                _telemetryMaxStepMs,
                snapPerSec,
                GC.GetTotalMemory(false) / (1024.0 * 1024.0),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2));

            if (World is not null)
                LatestIntegrity = World.AuditIntegrity();

            _telemetryWindowSeconds = 0.0;
            _telemetryFramesInWindow = 0;
            _telemetryTicksInWindow = 0;
            _telemetrySnapshotsInWindow = 0;
            _telemetryTotalTickMsInWindow = 0.0;
            _telemetryStepsInWindow = 0;
            _telemetryMaxStepMs = 0.0;
        }
    }
}
