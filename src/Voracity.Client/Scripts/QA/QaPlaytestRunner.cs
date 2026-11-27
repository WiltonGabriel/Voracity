using System.Diagnostics;
using Godot;
using Voracity.Core.Models;

namespace Voracity.Client.QA;

/// <summary>
/// Automated visual &amp; interactive QA runner triggered via --qa-tour=&lt;dir&gt; or --qa-5y-soak=&lt;dir&gt;.
/// Captures real Vulkan screenshots, measures live Godot _Process speeds (1x-5x), simulates 5 years, and verifies UI bounds.
/// </summary>
public partial class QaPlaytestRunner : Node
{
    private readonly string _outputDir;
    private readonly Func<string, object?> _stepAction;
    private readonly bool _soakMode;
    private readonly List<string> _reportLines = new();

    private int _stage;
    private int _waitFrames;

    // Live per-speed measurement state for soakMode
    private int _activeMeasureSpeed = 1;
    private double _measureElapsedSec;
    private int _measureFrames;
    private int _measureStartTicks;

    public QaPlaytestRunner(string outputDir, Func<string, object?> stepAction, bool soakMode = false)
    {
        _outputDir = outputDir;
        _stepAction = stepAction;
        _soakMode = soakMode;
    }

    public override void _Ready()
    {
        Directory.CreateDirectory(_outputDir);
        _waitFrames = 4;
    }

    public override void _Process(double delta)
    {
        if (_soakMode)
        {
            ProcessSoakMode(delta);
            return;
        }

        if (_waitFrames > 0)
        {
            _waitFrames--;
            return;
        }

        switch (_stage)
        {
            case 0:
                Capture("01_main_menu.png");
                _stepAction("open_settings");
                _stage++;
                _waitFrames = 4;
                break;
            case 1:
                Capture("02_settings_modal.png");
                _stepAction("close_settings_open_nations");
                _stage++;
                _waitFrames = 4;
                break;
            case 2:
                Capture("03_nation_select.png");
                _stepAction("start_campaign_AET");
                _stage++;
                _waitFrames = 6;
                break;
            case 3:
                Capture("04_map_default_zoom.png");
                _stepAction("select_capital_and_zoom_in");
                _stage++;
                _waitFrames = 6;
                break;
            case 4:
                Capture("05_map_zoomed_province_selected.png");
                _stepAction("upgrade_holding_and_open_politics");
                _stage++;
                _waitFrames = 6;
                break;
            case 5:
                Capture("06_drawer_and_simulation.png");
                _stepAction("switch_lens_economic_and_zoom_out");
                _stage++;
                _waitFrames = 6;
                break;
            case 6:
                Capture("07_lens_economic_strategic.png");
                var editorStats = _stepAction("open_map_editor_and_exercise")?.ToString() ?? string.Empty;
                _reportLines.Add($"[editor_check] {editorStats}");
                _stage++;
                _waitFrames = 6;
                break;
            case 7:
                Capture("08_map_editor_inspect_and_edit.png");
                File.WriteAllLines(Path.Combine(_outputDir, "qa_report.txt"), _reportLines);
                GD.Print($"[QA-TOUR] Completed 8 screenshots in {_outputDir}");
                GetTree().Quit();
                _stage++;
                break;
        }
    }

    private void ProcessSoakMode(double delta)
    {
        if (_waitFrames > 0)
        {
            _waitFrames--;
            return;
        }

        switch (_stage)
        {
            case 0:
                _stepAction("start_campaign_AET");
                _stepAction("enable_debug_session_and_unpause");
                _activeMeasureSpeed = 1;
                _stepAction($"set_speed:{_activeMeasureSpeed}");
                _measureElapsedSec = 0.0;
                _measureFrames = 0;
                _measureStartTicks = (int)(_stepAction("get_ticks") ?? 0);
                _stage++;
                break;

            case 1:
                _measureElapsedSec += delta;
                _measureFrames++;
                if (_measureElapsedSec >= 1.25)
                {
                    var endTicks = (int)(_stepAction("get_ticks") ?? 0);
                    var dticks = endTicks - _measureStartTicks;
                    var tps = dticks / _measureElapsedSec;
                    var fps = _measureFrames / _measureElapsedSec;
                    var line = $"[live_speed_{_activeMeasureSpeed}x] elapsed={_measureElapsedSec:F3}s frames={_measureFrames} fps={fps:F1} ticks={dticks} actual_tps={tps:F2}";
                    _reportLines.Add(line);
                    GD.Print(line);

                    if (_activeMeasureSpeed < 5)
                    {
                        _activeMeasureSpeed++;
                        _stepAction($"set_speed:{_activeMeasureSpeed}");
                        _measureElapsedSec = 0.0;
                        _measureFrames = 0;
                        _measureStartTicks = endTicks;
                    }
                    else
                    {
                        _stage++;
                        _waitFrames = 2;
                    }
                }
                break;

            case 2:
                var benchLine = _stepAction("debug_bench_speeds")?.ToString() ?? string.Empty;
                _reportLines.Add($"[bench] {benchLine}");

                var sw = Stopwatch.StartNew();
                var rep = _stepAction("advance_to_five_years") as SimulationIntegrityReport;
                sw.Stop();
                if (rep is not null)
                {
                    var gold = new MonetaryAmount(rep.TotalTreasuryCoppers).ToGold();
                    var line = $"[5y_audit] elapsed_ms={sw.Elapsed.TotalMilliseconds:F1} date={rep.Date.ToDisplayString()} ticks={rep.TotalTicksExecuted} " +
                               $"nations={rep.NationsCount} locs={rep.LocationsCount} mkts={rep.MarketsCount} pops={rep.ActivePopCount} pop_total={rep.TotalWorldPopulation:N0} " +
                               $"treasury_gold={gold:N0} ctrl_avg={rep.AverageControl:F1}% starving={rep.StarvingProvincesCount} price_range=[{rep.MinPriceRatio:F3}x..{rep.MaxPriceRatio:F3}x] " +
                               $"healthy={rep.IsHealthy} anomalies={rep.Anomalies.Count}";
                    _reportLines.Add(line);
                    GD.Print(line);
                }
                _stage++;
                _waitFrames = 6;
                break;

            case 3:
                Capture("09_debug_5years_live.png");
                File.WriteAllLines(Path.Combine(_outputDir, "qa_5y_report.txt"), _reportLines);
                GD.Print($"[QA-5Y-SOAK] Completed in {_outputDir}");
                GetTree().Quit();
                _stage++;
                break;
        }
    }

    private void Capture(string fileName)
    {
        var tex = GetViewport().GetTexture();
        var img = tex.GetImage();
        var path = Path.Combine(_outputDir, fileName);
        img.SavePng(path);
        var boundsInfo = _stepAction($"inspect:{fileName}")?.ToString() ?? string.Empty;
        _reportLines.Add($"[{fileName}] size={img.GetWidth()}x{img.GetHeight()} {boundsInfo}");
    }
}
