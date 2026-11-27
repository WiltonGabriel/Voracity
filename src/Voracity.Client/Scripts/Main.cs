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
/// Hybrid Godot 4 Scene Coordinator backed by Main.tscn.
/// Binds CameraController2D, MapView2D, HudRoot (.tscn), MainMenu (.tscn), and MapEditorView via [Export].
/// </summary>
public partial class Main : Node2D
{
    [Export] public CameraController2D Camera { get; set; } = null!;
    [Export] public MapView2D MapView { get; set; } = null!;
    [Export] public HudRoot Hud { get; set; } = null!;
    [Export] public MainMenuView MainMenu { get; set; } = null!;
    [Export] public MapEditorView MapEditor { get; set; } = null!;

    private WorldDatabase _db = null!;
    private string _dataRoot = string.Empty;
    private string _vmapCacheDir = string.Empty;
    private GameSettings _settings = null!;
    private readonly CampaignRunner _runner = new();

    public override void _Ready()
    {
        _dataRoot = ResolveDataRoot();
        _vmapCacheDir = Path.Combine(SettingsStorage.GetDefaultUserDirectory(), "cache", "vmap");
        _db = ModdingDataLoader.Load(_dataRoot);
        _settings = SettingsStorage.Load();
        if (OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).Any(a => a == "--debug-mode"))
            _settings = _settings with { DebugMode = true };

        Camera ??= GetNode<CameraController2D>("Camera");
        MapView ??= GetNode<MapView2D>("MapView2D");
        Hud ??= GetNode<HudRoot>("HudRoot");
        MainMenu ??= GetNode<MainMenuView>("MainMenu");
        MapEditor ??= GetNode<MapEditorView>("MapEditorView");

        Hud.Initialize(_db.Localization, _settings);
        Hud.Visible = false;

        MapEditor.Initialize(MapView, _db.Localization);
        MapEditor.Visible = false;

        MainMenu.Initialize(_db, _settings);

        MapView.Visible = false;
        MapView.ConfigureEnvironment(_db.GeographyFeatures, _vmapCacheDir);
        MapView.SetCameraZoom(Camera.Zoom.X);
        Camera.ZoomChanged += zoom => MapView.SetCameraZoom(zoom);

        ApplySystemSettings(_settings);
        WireEvents();
        MaybeAttachQaRunner();
    }

    public override void _Process(double delta)
    {
        if (MainMenu.Visible)
            return;

        if (MapEditor.IsOpen)
        {
            Camera.HandleKeyboardPan(delta);
            return;
        }

        if (_runner.World is null)
            return;

        Camera.HandleKeyboardPan(delta);

        var (executed, needFullSnapshot) = _runner.StepFrame(delta, Hud.IsPauseMenuOpen);
        if (executed > 0)
        {
            if (needFullSnapshot)
                PushSnapshotToViews();
            else
                Hud.UpdateClockOnly(_runner.World.CurrentDate, _runner.World.IsPaused, _runner.World.Speed);
        }

        if (_settings.DebugMode && (Hud.IsDebugOverlayVisible || executed > 0))
            Hud.UpdateDebugTelemetry(_runner.LatestTelemetry, null, _runner.LatestIntegrity);
    }

    public override void _UnhandledInput(InputEvent evt)
    {
        if (MainMenu.Visible)
            return;

        if (MapEditor.IsOpen)
        {
            Camera.HandlePointerInput(evt);
            return;
        }

        if (_runner.World is null)
            return;

        if (evt is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Hud.TogglePauseMenu();
            return;
        }

        if (Hud.IsPauseMenuOpen)
            return;

        if (Camera.HandlePointerInput(evt))
            return;

        if (evt is InputEventKey { Pressed: true, Keycode: Key.F3 } && _settings.DebugMode)
        {
            Hud.ToggleDebugOverlay();
            if (Hud.IsDebugOverlayVisible)
                Hud.UpdateDebugTelemetry(_runner.LatestTelemetry, _runner.World.BuildRenderSnapshot(), _runner.LatestIntegrity);
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
        MainMenu.StartNewCampaignRequested += nationId =>
        {
            var world = new SimulationWorld(_db, playerNationId: nationId);
            world.EnqueueCommand(new SetSpeedCommand(_settings.DefaultSpeed));
            world.DrainCommandsImmediate();
            EnterCampaign(world);
        };

        MainMenu.LoadCampaignRequested += saveFilePath =>
        {
            var saveData = SaveGameStorage.Load(saveFilePath);
            EnterCampaign(SimulationWorld.FromSaveData(_db, saveData));
        };

        MainMenu.SettingsChanged += ApplySystemSettings;
        Hud.SettingsChanged += ApplySystemSettings;

        MainMenu.MapEditorRequested += () =>
        {
            _runner.ClearWorld();
            Hud.Visible = false;
            MainMenu.Visible = false;
            Camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.92f);
            MapEditor.OpenEditor(_dataRoot, _settings.ResolveActiveLanguage());
        };
        MapEditor.ExitEditorRequested += () =>
        {
            MainMenu.RefreshOnShow(_settings);
            MainMenu.Visible = true;
        };
        MapEditor.WorldDataSavedRequested += () =>
        {
            _db = ModdingDataLoader.Load(_dataRoot);
            MapView.ConfigureEnvironment(_db.GeographyFeatures, _vmapCacheDir);
            MapView.ResetMap();
        };
        MapView.EditorCanvasClicked += MapEditor.HandleCanvasClick;
        MapView.EditorCanvasDragged += MapEditor.HandleCanvasDrag;

        MapView.ProvinceSelected += (locId, stateId) => Hud.SelectProvince(locId, stateId);
        MapView.ProvinceHovered += loc => Hud.SetHoverProvince(loc);
        Hud.LensModeChanged += mode => MapView.SetLensMode(mode);
        Hud.FocusStateRequested += stateId =>
        {
            if (MapView.TryGetStateCenter(stateId, out var pos))
                Camera.FocusWorldPosition(pos);
            MapView.SetSelectedState(stateId);
        };
        Hud.CommandIssued += cmd =>
        {
            if (_runner.World is null)
                return;
            _runner.World.EnqueueCommand(cmd);
            _runner.World.DrainCommandsImmediate();
            PushSnapshotToViews();
        };
        Hud.QuicksaveRequested += () =>
        {
            if (_runner.World is null)
                return;
            var saveName = $"{_runner.World.PlayerNationId}_{_runner.World.CurrentDate.Year}_{_runner.World.CurrentDate.Month:00}";
            SaveGameStorage.Save(_runner.World.ExportSaveData(saveName));
            Hud.NotifySaveSucceeded();
        };
        Hud.ReturnToMainMenuRequested += () =>
        {
            _runner.ClearWorld();
            MapView.Visible = false;
            Hud.Visible = false;
            MainMenu.RefreshOnShow(_settings);
            MainMenu.Visible = true;
        };

        Hud.DebugFastForwardDaysRequested += days =>
        {
            var msg = _runner.FormatFastForwardReport(days);
            if (!string.IsNullOrEmpty(msg))
            {
                PushSnapshotToViews();
                Hud.SetDebugReportText(msg);
                GD.Print($"[DEBUG] {msg}");
            }
        };
        Hud.DebugBenchmarkSpeedsRequested += () =>
        {
            var msg = _runner.FormatBenchmarkReport(_db);
            Hud.SetDebugReportText(msg);
            GD.Print($"[DEBUG-BENCH] {msg}");
        };
        Hud.DebugAuditWorldRequested += () =>
        {
            var msg = _runner.FormatIntegrityAuditReport();
            if (!string.IsNullOrEmpty(msg) && _runner.World is not null)
            {
                Hud.UpdateDebugTelemetry(_runner.LatestTelemetry, _runner.World.BuildRenderSnapshot(), _runner.LatestIntegrity);
                Hud.SetDebugReportText(msg);
            }
        };
    }

    private void EnterCampaign(SimulationWorld world)
    {
        _runner.AttachWorld(world);
        MapView.ResetMap();
        MapView.SetPrebuiltTopology(world.Topology);
        MapView.Visible = true;
        Hud.SetSettings(_settings);
        Hud.Visible = true;
        MainMenu.Visible = false;
        PushSnapshotToViews();

        var snap = world.BuildRenderSnapshot();
        var cap = snap.States.FirstOrDefault(s => s.IsPlayerCapital);
        if (cap is not null && MapView.TryGetStateCenter(cap.StateId, out var capPos))
            Camera.FocusWorldPosition(capPos);
    }

    private void ApplySystemSettings(GameSettings updated)
    {
        _settings = updated;
        Hud?.SetSettings(updated);

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
        MapView.ApplySnapshot(snapshot);
        Hud.UpdateFromSnapshot(snapshot);
        _runner.NotifySnapshotPushed();

        if (_settings.DebugMode)
            Hud.UpdateDebugTelemetry(_runner.LatestTelemetry, snapshot, _runner.LatestIntegrity);
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
                MainMenu.QaOpenSettings();
                return "ok";
            case "close_settings_open_nations":
                MainMenu.QaCloseSettingsAndOpenNationSelect();
                return "ok";
            case "start_campaign_AET":
                var world = new SimulationWorld(_db, "AET");
                world.EnqueueCommand(new SetPausedCommand(true));
                world.DrainCommandsImmediate();
                EnterCampaign(world);
                Camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.84f);
                return "ok";
            case "select_capital_and_zoom_in":
                if (_runner.World is not null)
                {
                    var snap = _runner.World.BuildRenderSnapshot();
                    var capLoc = snap.Provinces.First(p => p.OwnerNationId == "AET" && p.IsCapital);
                    var pos = MapView.GetProvinceWorldPosition(capLoc.ProvinceId);
                    Camera.FocusWorldPosition(pos, 1.85f);
                    var clicked = MapView.TryClickWorldPosition(pos);
                    var sibling = snap.Provinces.FirstOrDefault(p => p.StateId == capLoc.StateId && !p.IsCapital);
                    Hud.SetHoverProvince(sibling);
                    return $"clicked={clicked} selProv={Hud.SelectedProvinceId}";
                }
                return "ok";
            case "upgrade_holding_and_open_politics":
                if (_runner.World is not null)
                {
                    if (Hud.SelectedProvinceId is not null)
                    {
                        _runner.World.EnqueueCommand(new UpgradeLocationCommand(Hud.SelectedProvinceId, LocationUpgradeKind.ExpandRgo));
                        _runner.World.EnqueueCommand(new UpgradeLocationCommand(Hud.SelectedProvinceId, LocationUpgradeKind.UpgradeRoad));
                    }
                    _runner.World.DrainCommandsImmediate();
                    _runner.World.StepDays(28);
                    PushSnapshotToViews();
                    Hud.SetHoverProvince(null);
                    Hud.QaOpenTab("Realm");
                }
                return "ok";
            case "switch_lens_economic_and_zoom_out":
                Hud.QaOpenTab("None");
                Hud.CloseStateInspector();
                MapView.SetLensMode(MapLensMode.RgoAndMarket);
                Hud.SetActiveLens(MapLensMode.RgoAndMarket);
                Camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.84f);
                return "ok";
            case "open_map_editor_and_exercise":
                _runner.ClearWorld();
                Hud.Visible = false;
                MainMenu.Visible = false;
                Camera.FocusWorldPosition(MapView2D.WorldCanvasSize * 0.50f, 0.92f);
                MapEditor.OpenEditor(_dataRoot, _settings.ResolveActiveLanguage());
                return MapEditor.QaExerciseEditorWorkflow();
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
                    Hud.SetDebugReportText(msg);
                    return _runner.World.AuditIntegrity();
                }
                return null;
            case "debug_bench_speeds":
                var bmsg = _runner.FormatBenchmarkReport(_db);
                Hud.SetDebugReportText(bmsg);
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
                    var bounds = Hud.Visible ? Hud.InspectLayoutBounds() : "hud=hidden";
                    return $"vp={vpSize.X}x{vpSize.Y} camPos={Camera.Position} camZoom={Camera.Zoom.X:F2} hudVisible={Hud.Visible} menuVisible={MainMenu.Visible} {bounds} worldDate={_runner.World?.CurrentDate.ToDisplayString() ?? "N/A"}";
                }
                return null;
        }
    }
}
