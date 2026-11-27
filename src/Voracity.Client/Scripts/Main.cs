using Godot;
using Voracity.Client.Camera;
using Voracity.Client.Campaign;
using Voracity.Client.Editor;
using Voracity.Client.Map;
using Voracity.Client.QA;
using Voracity.Client.UI;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Client;

/// <summary>
/// Thin Godot 4 scene root coordinating MainMenuView, MapView2D, HudRoot, MapEditorView,
/// CameraController2D, CampaignRunner, and QaPlaytestRunner.
/// </summary>
public partial class Main : Node2D
{
    private WorldDatabase _db = null!;
    private string _dataRoot = string.Empty;
    private string _vmapCacheDir = string.Empty;
    private GameSettings _settings = null!;

    private CameraController2D _camera = null!;
    private MapView2D _mapView = null!;
    private HudRoot _hud = null!;
    private MainMenuView _mainMenu = null!;
    private MapEditorView _mapEditor = null!;
    private readonly CampaignRunner _runner = new();

    public override void _Ready()
    {
        _dataRoot = ResolveDataRoot();
        _vmapCacheDir = Path.Combine(SettingsStorage.GetDefaultUserDirectory(), "cache", "vmap");
        _db = ModdingDataLoader.Load(_dataRoot);
        _settings = SettingsStorage.Load();
        if (OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).Any(a => a == "--debug-mode"))
            _settings = _settings with { DebugMode = true };
        ApplySystemSettings(_settings);

        _camera = new CameraController2D(MapView2D.WorldCanvasSize, MapView2D.MinCameraZoom, MapView2D.MaxCameraZoom);
        AddChild(_camera);

        _mapView = new MapView2D { Visible = false };
        AddChild(_mapView);
        _mapView.ConfigureEnvironment(_db.GeographyFeatures, _vmapCacheDir);
        _mapView.SetCameraZoom(_camera.Zoom.X);
        _camera.ZoomChanged += zoom => _mapView.SetCameraZoom(zoom);

        _hud = new HudRoot(_db.Localization, _settings) { Visible = false };
        AddChild(_hud);

        _mapEditor = new MapEditorView(_mapView, _db.Localization) { Visible = false };
        AddChild(_mapEditor);

        _mainMenu = new MainMenuView(_db, _settings);
        AddChild(_mainMenu);

        WireEvents();
        MaybeAttachQaRunner();
    }

    public override void _Process(double delta)
    {
        if (_mainMenu.Visible)
            return;

        if (_mapEditor.IsOpen)
        {
            _camera.HandleKeyboardPan(delta);
            return;
        }

        if (_runner.World is null)
            return;

        _camera.HandleKeyboardPan(delta);

        var (executed, needFullSnapshot) = _runner.StepFrame(delta, _hud.IsPauseMenuOpen);
        if (executed > 0)
        {
            if (needFullSnapshot)
                PushSnapshotToViews();
            else
                _hud.UpdateClockOnly(_runner.World.CurrentDate, _runner.World.IsPaused, _runner.World.Speed);
        }

        if (_settings.DebugMode && (_hud.IsDebugOverlayVisible || executed > 0))
            _hud.UpdateDebugTelemetry(_runner.LatestTelemetry, null, _runner.LatestIntegrity);
    }

    public override void _UnhandledInput(InputEvent evt)
    {
        if (_mainMenu.Visible)
            return;

        if (_mapEditor.IsOpen)
        {
            _camera.HandlePointerInput(evt);
            return;
        }

        if (_runner.World is null)
            return;

        if (evt is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            _hud.TogglePauseMenu();
            return;
        }

        if (_hud.IsPauseMenuOpen)
            return;

        if (_camera.HandlePointerInput(evt))
            return;

        if (evt is InputEventKey { Pressed: true, Keycode: Key.F3 } && _settings.DebugMode)
        {
            _hud.ToggleDebugOverlay();
            if (_hud.IsDebugOverlayVisible)
                _hud.UpdateDebugTelemetry(_runner.LatestTelemetry, _runner.World.BuildRenderSnapshot(), _runner.LatestIntegrity);
        }
        else if (evt is InputEventKey { Pressed: true, Keycode: Key.Space })
        {
            _runner.World.EnqueueCommand(new SetPausedCommand(!_runner.World.IsPaused));
            _runner.World.DrainCommandsImmediate();
            PushSnapshotToViews();
        }
    }

    private void WireEvents()
    {
        _mainMenu.StartNewCampaignRequested += nationId =>
        {
            var world = new SimulationWorld(_db, playerNationId: nationId);
            world.EnqueueCommand(new SetSpeedCommand(_settings.DefaultSpeed));
            world.DrainCommandsImmediate();
            EnterCampaign(world);
        };

        _mainMenu.LoadCampaignRequested += saveFilePath =>
        {
            var saveData = SaveGameStorage.Load(saveFilePath);
            EnterCampaign(SimulationWorld.FromSaveData(_db, saveData));
        };

        _mainMenu.SettingsChanged += ApplySystemSettings;
        _hud.SettingsChanged += ApplySystemSettings;

        _mainMenu.MapEditorRequested += () =>
        {
            _runner.ClearWorld();
            _hud.Visible = false;
            _mainMenu.Visible = false;
            _camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.92f);
            _mapEditor.OpenEditor(_dataRoot, _settings.ResolveActiveLanguage());
        };
        _mapEditor.ExitEditorRequested += () =>
        {
            _mainMenu.RefreshOnShow(_settings);
            _mainMenu.Visible = true;
        };
        _mapEditor.WorldDataSavedRequested += () =>
        {
            _db = ModdingDataLoader.Load(_dataRoot);
            _mapView.ConfigureEnvironment(_db.GeographyFeatures, _vmapCacheDir);
            _mapView.ResetMap();
        };
        _mapView.EditorCanvasClicked += _mapEditor.HandleCanvasClick;
        _mapView.EditorCanvasDragged += _mapEditor.HandleCanvasDrag;

        _mapView.ProvinceSelected += (locId, stateId) => _hud.SelectProvince(locId, stateId);
        _mapView.ProvinceHovered += loc => _hud.SetHoverProvince(loc);
        _hud.LensModeChanged += mode => _mapView.SetLensMode(mode);
        _hud.FocusStateRequested += stateId =>
        {
            if (_mapView.TryGetStateCenter(stateId, out var pos))
                _camera.FocusWorldPosition(pos);
            _mapView.SetSelectedState(stateId);
        };
        _hud.CommandIssued += cmd =>
        {
            if (_runner.World is null)
                return;
            _runner.World.EnqueueCommand(cmd);
            _runner.World.DrainCommandsImmediate();
            PushSnapshotToViews();
        };
        _hud.QuicksaveRequested += () =>
        {
            if (_runner.World is null)
                return;
            var saveName = $"{_runner.World.PlayerNationId}_{_runner.World.CurrentDate.Year}_{_runner.World.CurrentDate.Month:00}";
            SaveGameStorage.Save(_runner.World.ExportSaveData(saveName));
            _hud.NotifySaveSucceeded();
        };
        _hud.ReturnToMainMenuRequested += () =>
        {
            _runner.ClearWorld();
            _mapView.Visible = false;
            _hud.Visible = false;
            _mainMenu.RefreshOnShow(_settings);
            _mainMenu.Visible = true;
        };

        _hud.DebugFastForwardDaysRequested += days =>
        {
            var msg = _runner.FormatFastForwardReport(days);
            if (!string.IsNullOrEmpty(msg))
            {
                PushSnapshotToViews();
                _hud.SetDebugReportText(msg);
                GD.Print($"[DEBUG] {msg}");
            }
        };
        _hud.DebugBenchmarkSpeedsRequested += () =>
        {
            var msg = _runner.FormatBenchmarkReport(_db);
            _hud.SetDebugReportText(msg);
            GD.Print($"[DEBUG-BENCH] {msg}");
        };
        _hud.DebugAuditWorldRequested += () =>
        {
            var msg = _runner.FormatIntegrityAuditReport();
            if (!string.IsNullOrEmpty(msg) && _runner.World is not null)
            {
                _hud.UpdateDebugTelemetry(_runner.LatestTelemetry, _runner.World.BuildRenderSnapshot(), _runner.LatestIntegrity);
                _hud.SetDebugReportText(msg);
            }
        };
    }

    private void EnterCampaign(SimulationWorld world)
    {
        _runner.AttachWorld(world);
        _mapView.ResetMap();
        _mapView.SetPrebuiltTopology(world.Topology);
        _mapView.Visible = true;
        _hud.SetSettings(_settings);
        _hud.Visible = true;
        _mainMenu.Visible = false;
        PushSnapshotToViews();

        var snap = world.BuildRenderSnapshot();
        var cap = snap.States.FirstOrDefault(s => s.IsPlayerCapital);
        if (cap is not null && _mapView.TryGetStateCenter(cap.StateId, out var capPos))
            _camera.FocusWorldPosition(capPos);
    }

    private void ApplySystemSettings(GameSettings updated)
    {
        _settings = updated;
        _hud?.SetSettings(updated);

        if (DisplayServer.GetName() != "headless")
        {
            var winMode = updated.WindowMode switch
            {
                "fullscreen" => DisplayServer.WindowMode.Fullscreen,
                "borderless" => DisplayServer.WindowMode.ExclusiveFullscreen,
                _ => DisplayServer.WindowMode.Windowed
            };
            DisplayServer.WindowSetMode(winMode);
            DisplayServer.WindowSetVsyncMode(updated.Vsync
                ? DisplayServer.VSyncMode.Enabled
                : DisplayServer.VSyncMode.Disabled);
            GetWindow().ContentScaleFactor = Math.Clamp(updated.UiScale, 0.8f, 1.5f);
        }

        AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb(Math.Clamp(updated.MasterVolume, 0.01f, 1.0f)));
    }

    private void PushSnapshotToViews()
    {
        if (_runner.World is null)
            return;

        var snapshot = _runner.World.BuildRenderSnapshot();
        _mapView.ApplySnapshot(snapshot);
        _hud.UpdateFromSnapshot(snapshot);
        _runner.NotifySnapshotPushed();

        if (_settings.DebugMode)
            _hud.UpdateDebugTelemetry(_runner.LatestTelemetry, snapshot, _runner.LatestIntegrity);
    }

    private static string ResolveDataRoot()
    {
        var dir = ProjectSettings.GlobalizePath("res://../../data");
        if (Directory.Exists(Path.Combine(dir, "base")))
            return dir;

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data"));
    }

    private void MaybeAttachQaRunner()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        var tourArg = args.FirstOrDefault(a => a.StartsWith("--qa-tour=", StringComparison.Ordinal));
        if (tourArg is not null)
        {
            var runner = new QaPlaytestRunner(tourArg["--qa-tour=".Length..], HandleQaAction, soakMode: false);
            AddChild(runner);
            return;
        }

        var soakArg = args.FirstOrDefault(a => a.StartsWith("--qa-5y-soak=", StringComparison.Ordinal));
        if (soakArg is not null)
        {
            var runner = new QaPlaytestRunner(soakArg["--qa-5y-soak=".Length..], HandleQaAction, soakMode: true);
            AddChild(runner);
        }
    }

    private object? HandleQaAction(string action)
    {
        switch (action)
        {
            case "open_settings":
                _mainMenu.QaOpenSettings();
                return "ok";
            case "close_settings_open_nations":
                _mainMenu.QaCloseSettingsAndOpenNationSelect();
                return "ok";
            case "start_campaign_AET":
                var world = new SimulationWorld(_db, "AET");
                world.EnqueueCommand(new SetPausedCommand(true));
                world.DrainCommandsImmediate();
                EnterCampaign(world);
                _camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.84f);
                return "ok";
            case "select_capital_and_zoom_in":
                if (_runner.World is not null)
                {
                    var snap = _runner.World.BuildRenderSnapshot();
                    var capLoc = snap.Provinces.First(p => p.OwnerNationId == "AET" && p.IsCapital);
                    var pos = _mapView.GetProvinceWorldPosition(capLoc.ProvinceId);
                    _camera.FocusWorldPosition(pos, 1.85f);
                    var clicked = _mapView.TryClickWorldPosition(pos);
                    var sibling = snap.Provinces.FirstOrDefault(p => p.StateId == capLoc.StateId && !p.IsCapital);
                    _hud.SetHoverProvince(sibling);
                    return $"clicked={clicked} selProv={_hud.SelectedProvinceId}";
                }
                return "ok";
            case "upgrade_holding_and_open_politics":
                if (_runner.World is not null)
                {
                    if (_hud.SelectedProvinceId is not null)
                    {
                        _runner.World.EnqueueCommand(new UpgradeLocationCommand(_hud.SelectedProvinceId, LocationUpgradeKind.ExpandRgo));
                        _runner.World.EnqueueCommand(new UpgradeLocationCommand(_hud.SelectedProvinceId, LocationUpgradeKind.UpgradeRoad));
                    }
                    _runner.World.DrainCommandsImmediate();
                    _runner.World.StepDays(28);
                    PushSnapshotToViews();
                    _hud.SetHoverProvince(null);
                    _hud.QaOpenTab("Realm");
                }
                return "ok";
            case "switch_lens_economic_and_zoom_out":
                _hud.QaOpenTab("None");
                _hud.CloseStateInspector();
                _mapView.SetLensMode(MapLensMode.RgoAndMarket);
                _hud.SetActiveLens(MapLensMode.RgoAndMarket);
                _camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.84f);
                return "ok";
            case "open_map_editor_and_exercise":
                _runner.ClearWorld();
                _hud.Visible = false;
                _mainMenu.Visible = false;
                _camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.92f);
                _mapEditor.OpenEditor(_dataRoot, _settings.ResolveActiveLanguage());
                return _mapEditor.QaExerciseEditorWorkflow();
            case "enable_debug_session_and_unpause":
                ApplySystemSettings(_settings with { DebugMode = true });
                if (_runner.World is not null)
                {
                    _runner.World.EnqueueCommand(new SetPausedCommand(false));
                    _runner.World.DrainCommandsImmediate();
                    _runner.ResetAccumulator();
                    PushSnapshotToViews();
                }
                return "ok";
            case "get_ticks":
                return _runner.World?.TotalTicksExecuted ?? 0;
            case "advance_to_five_years":
                if (_runner.World is not null)
                {
                    _runner.World.EnqueueCommand(new SetPausedCommand(true));
                    _runner.World.DrainCommandsImmediate();
                    var remainingTicks = Math.Max(0, 5 * 360 * SimulationWorld.SubTicksPerDay - _runner.World.TotalTicksExecuted);
                    var remainingDays = (remainingTicks + SimulationWorld.SubTicksPerDay - 1) / SimulationWorld.SubTicksPerDay;
                    var msg = remainingDays > 0 ? _runner.FormatFastForwardReport(remainingDays) : _runner.FormatIntegrityAuditReport();
                    PushSnapshotToViews();
                    _hud.SetDebugReportText(msg);
                    return _runner.World.AuditIntegrity();
                }
                return null;
            case "debug_bench_speeds":
                var bmsg = _runner.FormatBenchmarkReport(_db);
                _hud.SetDebugReportText(bmsg);
                return bmsg;
            default:
                if (action.StartsWith("set_speed:", StringComparison.Ordinal) &&
                    int.TryParse(action["set_speed:".Length..], out var reqSpd) &&
                    _runner.World is not null)
                {
                    _runner.World.EnqueueCommand(new SetSpeedCommand(reqSpd));
                    _runner.World.EnqueueCommand(new SetPausedCommand(false));
                    _runner.World.DrainCommandsImmediate();
                    _runner.ResetAccumulator();
                    return $"speed={_runner.World.Speed} target={CampaignRunner.GetTargetSubTicksPerSecond(_runner.World.Speed):F1}";
                }
                if (action.StartsWith("inspect:", StringComparison.Ordinal))
                {
                    var vpSize = GetViewport().GetVisibleRect().Size;
                    var bounds = _hud.Visible ? _hud.InspectLayoutBounds() : "hud=hidden";
                    return $"vp={vpSize.X}x{vpSize.Y} camPos={_camera.Position} camZoom={_camera.Zoom.X:F2} hudVisible={_hud.Visible} menuVisible={_mainMenu.Visible} {bounds} worldDate={_runner.World?.CurrentDate.ToDisplayString() ?? "N/A"}";
                }
                return null;
        }
    }
}
