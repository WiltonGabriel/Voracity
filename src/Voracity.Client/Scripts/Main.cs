using Godot;
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
    private GameSettings _settings = null!;
    private SimulationWorld? _world;

    private Camera2D _camera = null!;
    private MapView2D _mapView = null!;
    private HudRoot _hud = null!;
    private MainMenuView _mainMenu = null!;

    private double _tickAccumulatorSeconds;
    private bool _draggingCamera;

    public override void _Ready()
    {
        var dataRoot = ResolveDataRoot();
        _db = ModdingDataLoader.Load(dataRoot);
        _settings = SettingsStorage.Load();
        ApplySystemSettings(_settings);

        _camera = new Camera2D
        {
            Position = MapView2D.WorldCanvasSize * 0.50f,
            Zoom = new Vector2(0.92f, 0.92f),
            LimitLeft = 0,
            LimitTop = 0,
            LimitRight = (int)MapView2D.WorldCanvasSize.X,
            LimitBottom = (int)MapView2D.WorldCanvasSize.Y,
            Enabled = true
        };
        AddChild(_camera);

        _mapView = new MapView2D { Visible = false };
        AddChild(_mapView);
        _mapView.SetCameraZoom(_camera.Zoom.X);

        _hud = new HudRoot(_db.Localization, _settings) { Visible = false };
        AddChild(_hud);

        _mainMenu = new MainMenuView(_db, _settings);
        AddChild(_mainMenu);

        WireEvents();

        MaybeAttachQaRunner();
    }

    public override void _Process(double delta)
    {
        if (_world is null || _mainMenu.Visible)
            return;

        HandleCameraKeyboardPan(delta);

        if (_world.IsPaused || _hud.IsPauseMenuOpen)
            return;

        var subTicksPerSecond = _world.Speed switch
        {
            1 => 1.0,
            2 => 2.5,
            3 => 6.0,
            4 => 12.0,
            _ => 24.0
        };

        _tickAccumulatorSeconds += delta * subTicksPerSecond;
        var steps = (int)Math.Floor(_tickAccumulatorSeconds);
        if (steps <= 0)
            return;

        _tickAccumulatorSeconds -= steps;
        _world.StepSubTicks(Math.Min(steps, 16));
        PushSnapshotToViews();
    }

    public override void _UnhandledInput(InputEvent evt)
    {
        if (_world is null || _mainMenu.Visible)
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
            else if (mb.ButtonIndex == MouseButton.Right)
                _draggingCamera = mb.Pressed;
        }
        else if (evt is InputEventMouseMotion motion && _draggingCamera)
        {
            _camera.Position -= motion.Relative / _camera.Zoom;
        }
        else if (evt is InputEventKey { Pressed: true, Keycode: Key.Space })
        {
            _world.EnqueueCommand(new SetPausedCommand(!_world.IsPaused));
            PushSnapshotToViews();
        }
    }

    private void WireEvents()
    {
        _mainMenu.StartNewCampaignRequested += nationId =>
        {
            var world = new SimulationWorld(_db, playerNationId: nationId);
            world.EnqueueCommand(new SetSpeedCommand(_settings.DefaultSpeed));
            world.StepSubTicks(0);
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
        var snapshot = _world.BuildRenderSnapshot();
        _mapView.ApplySnapshot(snapshot);
        _hud.UpdateFromSnapshot(snapshot);
    }

    private void HandleCameraKeyboardPan(double delta)
    {
        var dir = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) dir.Y -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) dir.Y += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) dir.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) dir.X += 1;

        if (dir != Vector2.Zero)
            _camera.Position += dir.Normalized() * (float)(650.0 * delta) / _camera.Zoom.X;
    }

    private void AdjustZoom(float factor)
    {
        var next = Mathf.Clamp(_camera.Zoom.X * factor, 0.85f, 3.6f);
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
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs());
        var tourArg = args.FirstOrDefault(a => a.StartsWith("--qa-tour=", StringComparison.Ordinal));
        if (tourArg is null)
            return;

        var outDir = tourArg["--qa-tour=".Length..];
        var runner = new QaPlaytestRunner(outDir, HandleQaAction);
        AddChild(runner);
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
                _camera.Zoom = new Vector2(0.92f, 0.92f);
                _mapView.SetCameraZoom(0.92f);
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
                _camera.Position = MapView2D.WorldCanvasSize * 0.50f;
                _camera.Zoom = new Vector2(0.90f, 0.90f);
                _mapView.SetCameraZoom(0.90f);
                return "ok";
            default:
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
