using Godot;

namespace Voracity.Client.QA;

/// <summary>
/// Automated visual &amp; interactive QA runner triggered exclusively when --qa-tour=&lt;dir&gt; is passed.
/// Captures real Vulkan screenshots of every screen, simulates clicks, verifies UI bounds, and exits cleanly.
/// </summary>
public partial class QaPlaytestRunner : Node
{
    private readonly string _outputDir;
    private readonly Func<string, object?> _stepAction;
    private readonly List<string> _reportLines = new();
    private int _stage;
    private int _waitFrames;

    public QaPlaytestRunner(string outputDir, Func<string, object?> stepAction)
    {
        _outputDir = outputDir;
        _stepAction = stepAction;
    }

    public override void _Ready()
    {
        Directory.CreateDirectory(_outputDir);
        _waitFrames = 4;
    }

    public override void _Process(double delta)
    {
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
                File.WriteAllLines(Path.Combine(_outputDir, "qa_report.txt"), _reportLines);
                GD.Print($"[QA-TOUR] Completed 7 screenshots in {_outputDir}");
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
