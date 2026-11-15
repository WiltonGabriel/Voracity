using System.Diagnostics;
using System.Text;
using Godot;
using Voracity.Client.Editor;
using Voracity.Client.Map;
using Voracity.Client.QA;
using Voracity.Client.UI;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Client;

/// <summary>
/// Code-First entrypoint for Godot 4 (Vulkan + C# .NET).
/// Coordinates Main Menu, Campaign Load/Save, Settings (live language/window/VSync/scale), MapView2D, and HudRoot.
/// </summary>
public partial class Main : Node2D
{
    private WorldDatabase _db = null!;
    private string _dataRoot = string.Empty;
    private GameSettings _settings = null!;
    private SimulationWorld? _world;

    private Camera2D _camera = null!;
    private MapView2D _mapView = null!;
    private HudRoot _hud = null!;
    private MainMenuView _mainMenu = null!;
    private MapEditorView _mapEditor = null!;

    private double _tickAccumulatorSeconds;
    private bool _draggingCamera;

    // Live simulation telemetry & adaptive snapshot throttling
    private double _telemetryWindowSeconds;
    private int _telemetryFramesInWindow;
    private int _telemetryTicksInWindow;
    private int _telemetrySnapshotsInWindow;
    private double _telemetryTotalTickMsInWindow;
    private int _telemetryStepsInWindow;
    private double _telemetryMaxStepMs;
    private double _lastStepMs;
    private double _secondsSinceLastSnapshotPush;
    private SimTelemetryMetrics _latestTelemetry;
    private SimulationIntegrityReport? _latestIntegrity;

    public static double GetTargetSubTicksPerSecond(int speed) => speed switch
    {
        1 => 2.0,
        2 => 5.0,
        3 => 12.0,
        4 => 28.0,
        _ => 96.0
    };

    public override void _Ready()
    {
        _dataRoot = ResolveDataRoot();
        _db = ModdingDataLoader.Load(_dataRoot);
        _settings = SettingsStorage.Load();
        if (OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).Any(a => a == "--debug-mode"))
            _settings = _settings with { DebugMode = true };
        ApplySystemSettings(_settings);

        _camera = new Camera2D
        {
            Position = MapView2D.WorldCanvasSize * 0.50f,
            Zoom = new Vector2(0.84f, 0.84f),
            LimitLeft = -1100,
            LimitTop = -800,
            LimitRight = (int)MapView2D.WorldCanvasSize.X + 1100,
            LimitBottom = (int)MapView2D.WorldCanvasSize.Y + 800,
            Enabled = true
        };
        AddChild(_camera);

        _mapView = new MapView2D { Visible = false };
        AddChild(_mapView);
        _mapView.ConfigureEnvironment(_db.GeographyFeatures, Path.Combine(_dataRoot, ".vmap_cache"));
        _mapView.SetCameraZoom(_camera.Zoom.X);

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
            HandleCameraKeyboardPan(delta);
            return;
        }

        if (_world is null)
            return;

        HandleCameraKeyboardPan(delta);

        var targetRate = GetTargetSubTicksPerSecond(_world.Speed);
        var executedNow = 0;
        var needFullSnapshot = false;
        var crossedMidnight = false;

        if (!_world.IsPaused && !_hud.IsPauseMenuOpen)
        {
            _tickAccumulatorSeconds = Math.Min(_tickAccumulatorSeconds + delta * targetRate, targetRate * 0.5);
            var plannedSteps = (int)Math.Floor(_tickAccumulatorSeconds);
            if (plannedSteps > 0)
            {
                var swStart = Stopwatch.GetTimestamp();
                var maxSteps = Math.Min(plannedSteps, 64);
                for (var s = 0; s < maxSteps; s++)
                {
                    _world.StepSubTicks(1);
                    executedNow++;

                    var d = _world.CurrentDate;
                    if (d.SubTick6h == 0)
                    {
                        crossedMidnight = true;
                        if (d.Day % 7 == 0 || d.Day == 1)
                            needFullSnapshot = true;
                    }

                    // Keep frame time responsive at Speed 5 (<= 10ms sim budget per frame)
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
        if (executedNow > 0)
        {
            if (needFullSnapshot || (crossedMidnight && _secondsSinceLastSnapshotPush >= 0.25))
                PushSnapshotToViews();
            else
                _hud.UpdateClockOnly(_world.CurrentDate, _world.IsPaused, _world.Speed);
        }

        UpdateTelemetryWindow(delta, targetRate);
    }

    private void UpdateTelemetryWindow(double delta, double targetRate)
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

            _latestTelemetry = new SimTelemetryMetrics(
                fps,
                frameMs,
                _world?.Speed ?? 1,
                _world?.IsPaused ?? true,
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

            if (_settings.DebugMode && _world is not null)
            {
                _latestIntegrity = _world.AuditIntegrity();
                _hud.UpdateDebugTelemetry(_latestTelemetry, null, _latestIntegrity);
            }

            _telemetryWindowSeconds = 0.0;
            _telemetryFramesInWindow = 0;
            _telemetryTicksInWindow = 0;
            _telemetrySnapshotsInWindow = 0;
            _telemetryTotalTickMsInWindow = 0.0;
            _telemetryStepsInWindow = 0;
            _telemetryMaxStepMs = 0.0;
        }
        else if (_settings.DebugMode && _hud.IsDebugOverlayVisible && executedNowIsZeroOrUpdated())
        {
            // Light refresh when hovering provinces while paused
            _hud.UpdateDebugTelemetry(_latestTelemetry, null, _latestIntegrity);
        }

        bool executedNowIsZeroOrUpdated() => _world is not null && _world.IsPaused;
    }

    public override void _UnhandledInput(InputEvent evt)
    {
        if (_mainMenu.Visible)
            return;

        if (_mapEditor.IsOpen)
        {
            if (evt is InputEventMouseButton emb)
            {
                if (emb.ButtonIndex == MouseButton.WheelUp && emb.Pressed)
                    AdjustZoom(1.12f);
                else if (emb.ButtonIndex == MouseButton.WheelDown && emb.Pressed)
                    AdjustZoom(0.89f);
                else if (emb.ButtonIndex is MouseButton.Right or MouseButton.Middle)
                    _draggingCamera = emb.Pressed;
            }
            else if (evt is InputEventMouseMotion emotion && _draggingCamera)
            {
                _camera.Position -= emotion.Relative / _camera.Zoom;
                ClampCameraPosition();
            }
            return;
        }

        if (_world is null)
            return;

        if (evt is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            _hud.TogglePauseMenu();
            return;
        }

        if (_hud.IsPauseMenuOpen)
            return;

        if (evt is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                AdjustZoom(1.12f);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                AdjustZoom(0.89f);
            else if (mb.ButtonIndex is MouseButton.Right or MouseButton.Middle)
                _draggingCamera = mb.Pressed;
        }
        else if (evt is InputEventMouseMotion motion && _draggingCamera)
        {
            _camera.Position -= motion.Relative / _camera.Zoom;
            ClampCameraPosition();
        }
        else if (evt is InputEventKey { Pressed: true, Keycode: Key.F3 } && _settings.DebugMode)
        {
            _hud.ToggleDebugOverlay();
            if (_hud.IsDebugOverlayVisible && _world is not null)
            {
                _latestIntegrity = _world.AuditIntegrity();
                _hud.UpdateDebugTelemetry(_latestTelemetry, _world.BuildRenderSnapshot(), _latestIntegrity);
            }
        }
        else if (evt is InputEventKey { Pressed: true, Keycode: Key.Space })
        {
            _world.EnqueueCommand(new SetPausedCommand(!_world.IsPaused));
            _world.DrainCommandsImmediate();
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
            var world = SimulationWorld.FromSaveData(_db, saveData);
            EnterCampaign(world);
        };

        _mainMenu.SettingsChanged += ApplySystemSettings;
        _hud.SettingsChanged += ApplySystemSettings;

        _mainMenu.MapEditorRequested += () =>
        {
            _world = null;
            _hud.Visible = false;
            _mainMenu.Visible = false;
            _camera.Position = MapView2D.WorldCanvasSize * 0.50f;
            _camera.Zoom = new Vector2(0.92f, 0.92f);
            _mapView.SetCameraZoom(0.92f);
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
            _mapView.ConfigureEnvironment(_db.GeographyFeatures, Path.Combine(_dataRoot, ".vmap_cache"));
            _mapView.ResetMap();
        };
        _mapView.EditorCanvasClicked += _mapEditor.HandleCanvasClick;
        _mapView.EditorCanvasDragged += _mapEditor.HandleCanvasDrag;

        _mapView.ProvinceSelected += (provId, stateId) => _hud.SelectProvince(provId, stateId);
        _mapView.ProvinceHovered += prov => _hud.SetHoverProvince(prov);
        _hud.LensModeChanged += mode => _mapView.SetLensMode(mode);
        _hud.FocusStateRequested += stateId =>
        {
            if (_mapView.TryGetStateCenter(stateId, out var pos))
                _camera.Position = pos;
            _mapView.SetSelectedState(stateId);
        };
        _hud.CommandIssued += cmd =>
        {
            if (_world is null)
                return;
            _world.EnqueueCommand(cmd);
            _world.DrainCommandsImmediate();
            PushSnapshotToViews();
        };
        _hud.QuicksaveRequested += () =>
        {
            if (_world is null)
                return;
            var saveName = $"{_world.PlayerNationId}_{_world.CurrentDate.Year}_{_world.CurrentDate.Month:00}";
            SaveGameStorage.Save(_world.ExportSaveData(saveName));
            _hud.NotifySaveSucceeded();
        };
        _hud.ReturnToMainMenuRequested += () =>
        {
            _world = null;
            _mapView.Visible = false;
            _hud.Visible = false;
            _mainMenu.RefreshOnShow(_settings);
            _mainMenu.Visible = true;
        };

        _hud.DebugFastForwardDaysRequested += days => _ = ExecuteDebugFastForward(days);
        _hud.DebugBenchmarkSpeedsRequested += () => _ = ExecuteDebugSpeedBenchmark();
        _hud.DebugAuditWorldRequested += () => _ = ExecuteDebugIntegrityAudit();
    }

    private string ExecuteDebugFastForward(int days)
    {
        if (_world is null)
            return string.Empty;

        var startPop = _world.AuditIntegrity().TotalWorldPopulation;
        var sw = Stopwatch.StartNew();
        _world.StepDays(days);
        sw.Stop();

        var report = _world.AuditIntegrity();
        _latestIntegrity = report;
        PushSnapshotToViews();

        var perDayMs = sw.Elapsed.TotalMilliseconds / Math.Max(1, days);
        var popDeltaPct = startPop > 0 ? ((report.TotalWorldPopulation - startPop) * 100.0 / startPop) : 0.0;
        var treasuryGold = new MonetaryAmount(report.TotalTreasuryCoppers).ToGold();
        var msg =
            $"+{days}d ({days * SimulationWorld.SubTicksPerDay} ticks) em {sw.Elapsed.TotalMilliseconds:F1} ms ({perDayMs:F3} ms/dia) | " +
            $"Data: {report.Date.ToDisplayString()} | Pop: {startPop:N0} -> {report.TotalWorldPopulation:N0} ({popDeltaPct:+0.00;-0.00;0.00}%) | " +
            $"Tesouro Global: {treasuryGold:N0} Ouro | Preços: [{report.MinPriceRatio:F2}x..{report.MaxPriceRatio:F2}x] | " +
            $"Techs concluídas: {report.CompletedTechCountTotal} | Integridade: {(report.IsHealthy ? "OK" : $"ERRO ({report.Anomalies.Count})")}";

        _hud.SetDebugReportText(msg);
        GD.Print($"[DEBUG] {msg}");
        return msg;
    }

    private string ExecuteDebugSpeedBenchmark()
    {
        // Measure raw engine throughput on a deterministic clone/world + live speed pacing table
        var sw = Stopwatch.StartNew();
        var benchWorld = new SimulationWorld(_db, _world?.PlayerNationId ?? "AET");
        benchWorld.StepDays(360); // 1 full year = 1440 sub-ticks
        sw.Stop();
        var engineTicksPerSec = (360 * SimulationWorld.SubTicksPerDay) / Math.Max(0.0001, sw.Elapsed.TotalSeconds);

        var sb = new StringBuilder();
        sb.Append($"Núcleo puro (1 ano / 1.440 ticks): {sw.Elapsed.TotalMilliseconds:F1} ms ({engineTicksPerSec:F0} ticks/s máx) | Escala real: ");
        for (var spd = 1; spd <= 5; spd++)
        {
            var tps = GetTargetSubTicksPerSecond(spd);
            var dps = tps / SimulationWorld.SubTicksPerDay;
            sb.Append($"{spd}x={tps:F0}t/s ({dps:F2}d/s)");
            if (spd < 5) sb.Append(" · ");
        }
        var msg = sb.ToString();
        _hud.SetDebugReportText(msg);
        GD.Print($"[DEBUG-BENCH] {msg}");
        return msg;
    }

    private string ExecuteDebugIntegrityAudit()
    {
        if (_world is null)
            return string.Empty;

        var report = _world.AuditIntegrity();
        _latestIntegrity = report;
        var treasuryGold = new MonetaryAmount(report.TotalTreasuryCoppers).ToGold();
        var msg = report.IsHealthy
            ? $"Auditoria OK ({report.Date.ToDisplayString()}): {report.NationsCount} nações, {report.ProvincesCount} províncias, {report.ActivePopsCount} pops ({report.TotalWorldPopulation:N0} hab), Tesouro global {treasuryGold:N0} Ouro, Preços [{report.MinPriceRatio:F2}x..{report.MaxPriceRatio:F2}x], Techs: {report.CompletedTechCountTotal}, Guerras: {report.ActiveWarsCount}."
            : $"ANOMALIAS ({report.Anomalies.Count}): {string.Join(" | ", report.Anomalies.Take(4))}";
        _hud.UpdateDebugTelemetry(_latestTelemetry, _world.BuildRenderSnapshot(), report);
        _hud.SetDebugReportText(msg);
        return msg;
    }

    private void EnterCampaign(SimulationWorld world)
    {
        _world = world;
        _tickAccumulatorSeconds = 0.0;
        _mapView.ResetMap();
        _mapView.Visible = true;
        _hud.SetSettings(_settings);
        _hud.Visible = true;
        _mainMenu.Visible = false;
        PushSnapshotToViews();

        var snap = _world.BuildRenderSnapshot();
        var cap = snap.States.FirstOrDefault(s => s.IsPlayerCapital);
        if (cap is not null && _mapView.TryGetStateCenter(cap.StateId, out var capPos))
            _camera.Position = capPos;
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
        if (_world is null)
            return;
        _secondsSinceLastSnapshotPush = 0.0;
        _telemetrySnapshotsInWindow++;
        var snapshot = _world.BuildRenderSnapshot();
        _mapView.ApplySnapshot(snapshot);
        _hud.UpdateFromSnapshot(snapshot);
        if (_settings.DebugMode)
        {
            _latestIntegrity = _world.AuditIntegrity();
            _hud.UpdateDebugTelemetry(_latestTelemetry, snapshot, _latestIntegrity);
        }
    }

    private void HandleCameraKeyboardPan(double delta)
    {
        var dir = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) dir.Y -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) dir.Y += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) dir.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) dir.X += 1;

        if (dir != Vector2.Zero)
        {
            _camera.Position += dir.Normalized() * (float)(650.0 * delta) / _camera.Zoom.X;
            ClampCameraPosition();
        }
    }

    private void ClampCameraPosition()
    {
        _camera.Position = new Vector2(
            Mathf.Clamp(_camera.Position.X, -560f, MapView2D.WorldCanvasSize.X + 560f),
            Mathf.Clamp(_camera.Position.Y, -360f, MapView2D.WorldCanvasSize.Y + 360f));
    }

    private void AdjustZoom(float factor)
    {
        var next = Mathf.Clamp(_camera.Zoom.X * factor, MapView2D.MinCameraZoom, MapView2D.MaxCameraZoom);
        _camera.Zoom = new Vector2(next, next);
        _mapView.SetCameraZoom(next);
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
            var outDir = tourArg["--qa-tour=".Length..];
            var runner = new QaPlaytestRunner(outDir, HandleQaAction, soakMode: false);
            AddChild(runner);
            return;
        }

        var soakArg = args.FirstOrDefault(a => a.StartsWith("--qa-5y-soak=", StringComparison.Ordinal));
        if (soakArg is not null)
        {
            var outDir = soakArg["--qa-5y-soak=".Length..];
            var runner = new QaPlaytestRunner(outDir, HandleQaAction, soakMode: true);
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
                _camera.Position = MapView2D.WorldCanvasSize * 0.50f;
                _camera.Zoom = new Vector2(0.84f, 0.84f);
                _mapView.SetCameraZoom(0.84f);
                return "ok";
            case "select_capital_and_zoom_in":
                if (_world is not null)
                {
                    var snap = _world.BuildRenderSnapshot();
                    var capProv = snap.Provinces.First(p => p.OwnerNationId == "AET" && p.IsCapital);
                    var pos = _mapView.GetProvinceWorldPosition(capProv.ProvinceId);
                    _camera.Position = pos;
                    _camera.Zoom = new Vector2(1.85f, 1.85f);
                    _mapView.SetCameraZoom(1.85f);
                    // Simulate clicking an exact raster coordinate inside the capital province + hovering neighbor
                    var clicked = _mapView.TryClickWorldPosition(pos);
                    var siblingProv = snap.Provinces.FirstOrDefault(p => p.StateId == capProv.StateId && !p.IsCapital);
                    _hud.SetHoverProvince(siblingProv);
                    return $"clicked={clicked} selProv={_hud.SelectedProvinceId}";
                }
                return "ok";
            case "upgrade_holding_and_open_politics":
                if (_world is not null)
                {
                    if (_hud.SelectedProvinceId is not null)
                    {
                        _world.EnqueueCommand(new UpgradeProvinceHoldingCommand(_hud.SelectedProvinceId, "holding"));
                        _world.EnqueueCommand(new UpgradeProvinceHoldingCommand(_hud.SelectedProvinceId, "fort"));
                    }
                    _world.EnqueueCommand(new DiplomaticActionCommand("AET", "HAS", DiplomaticActionKind.DeclareWar));
                    _world.DrainCommandsImmediate();
                    _world.StepDays(28);
                    PushSnapshotToViews();
                    _hud.SetHoverProvince(null);
                    _hud.QaOpenTab("Politics");
                }
                return "ok";
            case "switch_lens_economic_and_zoom_out":
                _hud.QaOpenTab("None");
                _hud.CloseStateInspector();
                _mapView.SetLensMode(MapLensMode.Economic);
                _hud.SetActiveLens(MapLensMode.Economic);
                _camera.Position = MapView2D.WorldCanvasSize * 0.50f;
                _camera.Zoom = new Vector2(0.84f, 0.84f);
                _mapView.SetCameraZoom(0.84f);
                return "ok";
            case "open_map_editor_and_exercise":
                _world = null;
                _hud.Visible = false;
                _mainMenu.Visible = false;
                _camera.Position = MapView2D.WorldCanvasSize * 0.50f;
                _camera.Zoom = new Vector2(0.92f, 0.92f);
                _mapView.SetCameraZoom(0.92f);
                _mapEditor.OpenEditor(_dataRoot, _settings.ResolveActiveLanguage());
                return _mapEditor.QaExerciseEditorWorkflow();
            case "enable_debug_session_and_unpause":
                ApplySystemSettings(_settings with { DebugMode = true });
                if (_world is not null)
                {
                    _world.EnqueueCommand(new SetPausedCommand(false));
                    _world.DrainCommandsImmediate();
                    _tickAccumulatorSeconds = 0.0;
                    PushSnapshotToViews();
                }
                return "ok";
            case "get_ticks":
                return _world?.TotalTicksExecuted ?? 0;
            case "advance_to_five_years":
                if (_world is not null)
                {
                    _world.EnqueueCommand(new SetPausedCommand(true));
                    _world.DrainCommandsImmediate();
                    var remainingTicks = Math.Max(0, 5 * 360 * SimulationWorld.SubTicksPerDay - _world.TotalTicksExecuted);
                    var remainingDays = (remainingTicks + SimulationWorld.SubTicksPerDay - 1) / SimulationWorld.SubTicksPerDay;
                    if (remainingDays > 0)
                        ExecuteDebugFastForward(remainingDays);
                    else
                        ExecuteDebugIntegrityAudit();
                    return _world.AuditIntegrity();
                }
                return null;
            case "debug_bench_speeds":
                return ExecuteDebugSpeedBenchmark();
            default:
                if (action.StartsWith("set_speed:", StringComparison.Ordinal) &&
                    int.TryParse(action["set_speed:".Length..], out var reqSpd) &&
                    _world is not null)
                {
                    _world.EnqueueCommand(new SetSpeedCommand(reqSpd));
                    _world.EnqueueCommand(new SetPausedCommand(false));
                    _world.DrainCommandsImmediate();
                    _tickAccumulatorSeconds = 0.0;
                    return $"speed={_world.Speed} target={GetTargetSubTicksPerSecond(_world.Speed):F1}";
                }
                if (action.StartsWith("inspect:", StringComparison.Ordinal))
                {
                    var vpSize = GetViewport().GetVisibleRect().Size;
                    var bounds = _hud.Visible ? _hud.InspectLayoutBounds() : "hud=hidden";
                    return $"vp={vpSize.X}x{vpSize.Y} camPos={_camera.Position} camZoom={_camera.Zoom.X:F2} hudVisible={_hud.Visible} menuVisible={_mainMenu.Visible} {bounds} worldDate={_world?.CurrentDate.ToDisplayString() ?? "N/A"}";
                }
                return null;
        }
    }
}
