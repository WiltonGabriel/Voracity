using Godot;
using Voracity.Client.UI.Hud;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

/// <summary>
/// Thin EU5 + CK3-styled HUD coordinator assembling modular UI views under Scripts/UI/Hud/.
/// </summary>
public partial class HudRoot : CanvasLayer
{
    private readonly LocalizationCatalog _loc;
    private GameSettings _settings;
    private RenderSnapshot? _latestSnapshot;
    private string _activeTab = "None";

    private TopBarView _topBar = null!;
    private LocationInspectorView _inspector = null!;
    private readonly MarketDrawerView _marketDrawer;
    private readonly PopsDrawerView _popsDrawer;
    private readonly RealmDrawerView _realmDrawer;
    private MapLensBarView _lensBar = null!;
    private HoverBannerView _hoverBanner = null!;
    private PauseMenuOverlay _pauseOverlay = null!;

    private PanelContainer _drawerPanel = null!;
    private Label _drawerTitle = null!;
    private VBoxContainer _drawerContent = null!;
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.Ordinal);

    private PanelContainer _outlinerPanel = null!;
    private Label _outlinerTitle = null!;
    private VBoxContainer _outlinerContent = null!;

    private DebugOverlayPanel _debugOverlay = null!;
    private NestedTooltipManager _tooltipManager = null!;
    private SettingsModal _settingsModal = null!;

    public event Action<GameCommand>? CommandIssued;
    public event Action<MapLensMode>? LensModeChanged;
    public event Action<string>? FocusStateRequested;
    public event Action? QuicksaveRequested;
    public event Action? ReturnToMainMenuRequested;
    public event Action<GameSettings>? SettingsChanged;
    public event Action<int>? DebugFastForwardDaysRequested;
    public event Action? DebugBenchmarkSpeedsRequested;
    public event Action? DebugAuditWorldRequested;

    public string? SelectedProvinceId => _inspector.SelectedLocationId;
    public bool IsPauseMenuOpen => _pauseOverlay.Visible || _settingsModal.Visible;
    public bool IsDebugOverlayVisible => _debugOverlay.Visible;

    public HudRoot(LocalizationCatalog loc, GameSettings settings)
    {
        _loc = loc;
        _settings = settings;
        _marketDrawer = new MarketDrawerView(loc);
        _popsDrawer = new PopsDrawerView(loc);
        _realmDrawer = new RealmDrawerView(loc);
    }

    public override void _Ready()
    {
        Layer = 10;
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var lang = _settings.ResolveActiveLanguage();

        _topBar = new TopBarView(_loc, lang);
        _topBar.CommandIssued += cmd => CommandIssued?.Invoke(cmd);
        _topBar.ConceptClicked += (id, pos) => _tooltipManager.ShowConcept(id, 0, pos);
        _topBar.DebugToggleRequested += ToggleDebugOverlay;
        _topBar.PauseMenuRequested += TogglePauseMenu;
        _topBar.RealmTabRequested += () => ToggleLeftTab("Realm");
        root.AddChild(_topBar);
        _topBar.SetDebugButtonVisible(_settings.DebugMode);

        BuildLeftNav(root, lang);
        BuildDrawer(root);
        BuildOutliner(root, lang);

        _inspector = new LocationInspectorView(_loc, lang);
        _inspector.CommandIssued += cmd => CommandIssued?.Invoke(cmd);
        root.AddChild(_inspector);

        _hoverBanner = new HoverBannerView();
        root.AddChild(_hoverBanner);

        _lensBar = new MapLensBarView(_loc, lang);
        _lensBar.LensChanged += mode => LensModeChanged?.Invoke(mode);
        root.AddChild(_lensBar);

        _debugOverlay = new DebugOverlayPanel(_loc, _settings);
        _debugOverlay.FastForwardDaysRequested += d => DebugFastForwardDaysRequested?.Invoke(d);
        _debugOverlay.BenchmarkSpeedsRequested += () => DebugBenchmarkSpeedsRequested?.Invoke();
        _debugOverlay.AuditWorldRequested += () => DebugAuditWorldRequested?.Invoke();
        _debugOverlay.CollapseRequested += () => _debugOverlay.Visible = false;
        root.AddChild(_debugOverlay);

        _pauseOverlay = new PauseMenuOverlay(_loc, lang);
        _pauseOverlay.QuicksaveRequested += () => QuicksaveRequested?.Invoke();
        _pauseOverlay.SettingsRequested += () => _settingsModal.Open(_settings);
        _pauseOverlay.MainMenuRequested += () => ReturnToMainMenuRequested?.Invoke();
        root.AddChild(_pauseOverlay);

        _settingsModal = new SettingsModal(_loc, _settings);
        _settingsModal.SettingsApplied += s => SettingsChanged?.Invoke(s);
        root.AddChild(_settingsModal);

        _tooltipManager = new NestedTooltipManager();
        _tooltipManager.SetLanguage(lang);
        root.AddChild(_tooltipManager);
    }

    public void SetSettings(GameSettings updated)
    {
        _settings = updated;
        var lang = updated.ResolveActiveLanguage();
        _topBar?.SetLanguage(lang);
        _topBar?.SetDebugButtonVisible(updated.DebugMode);
        _inspector?.SetLanguage(lang);
        _lensBar?.SetLanguage(lang);
        _pauseOverlay?.SetLanguage(lang);
        _tooltipManager?.SetLanguage(lang);
        _debugOverlay?.ApplySettings(updated);
        if (!updated.DebugMode && _debugOverlay is not null)
            _debugOverlay.Visible = false;

        if (_navButtons.Count > 0)
        {
            _navButtons["Realm"].Text = _loc.Get(lang, "tab.realm");
            _navButtons["Markets"].Text = _loc.Get(lang, "tab.markets");
            _navButtons["Pops"].Text = _loc.Get(lang, "tab.pops");
            _outlinerTitle.Text = _loc.Get(lang, "outliner.title");
        }

        if (_latestSnapshot is not null)
            UpdateFromSnapshot(_latestSnapshot);
    }

    public void UpdateFromSnapshot(RenderSnapshot snap)
    {
        _latestSnapshot = snap;
        _topBar.UpdateFromSnapshot(snap);
        RefreshOutliner(snap);
        _inspector.Refresh(snap);
        if (_drawerPanel.Visible)
            RefreshDrawerContent();
    }

    public void UpdateClockOnly(GameDate date, bool isPaused, int speed) =>
        _topBar.UpdateClockOnly(date, isPaused, speed);

    public void SelectProvince(string provinceId, string stateId) =>
        _inspector.Select(provinceId, stateId, _latestSnapshot);

    public void SetHoverProvince(ProvinceSnapshot? prov)
    {
        _hoverBanner.SetHoveredLocation(prov);
        if (prov is not null)
            _debugOverlay?.SetFocusProvince(prov);
    }

    public void SetActiveLens(MapLensMode mode) => _lensBar.SetActiveLens(mode);

    public void TogglePauseMenu()
    {
        if (_settingsModal.Visible)
        {
            _settingsModal.Visible = false;
            return;
        }

        if (_pauseOverlay.Visible)
            _pauseOverlay.Visible = false;
        else
            _pauseOverlay.Open();
    }

    public void NotifySaveSucceeded() => _pauseOverlay.NotifySaveSucceeded();

    public void ToggleDebugOverlay()
    {
        if (_debugOverlay is not null)
            _debugOverlay.Visible = !_debugOverlay.Visible;
    }

    public void UpdateDebugTelemetry(SimTelemetryMetrics metrics, RenderSnapshot? snapshot, SimulationIntegrityReport? integrity) =>
        _debugOverlay?.UpdateTelemetry(metrics, snapshot ?? _latestSnapshot, integrity);

    public void SetDebugReportText(string text) => _debugOverlay?.SetReportText(text);

    public void QaOpenTab(string tabName)
    {
        if (string.IsNullOrEmpty(tabName) || tabName == "None")
        {
            _activeTab = "None";
            _drawerPanel.Visible = false;
            foreach (var kv in _navButtons)
                UiThemeFactory.ApplyButtonHighlight(kv.Value, false);
            return;
        }

        if (_activeTab != tabName)
            ToggleLeftTab(tabName);
    }

    public void CloseStateInspector() => _inspector.Close();

    public string InspectLayoutBounds()
    {
        var topR = _topBar.GetGlobalRect();
        var lensR = _lensBar.GetGlobalRect();
        var navMin = _navButtons["Realm"].GetGlobalRect();
        return $"top=({topR.Position.X:F0},{topR.Position.Y:F0},{topR.Size.X:F0}x{topR.Size.Y:F0}) " +
               $"lens=({lensR.Position.X:F0},{lensR.Position.Y:F0},{lensR.Size.X:F0}x{lensR.Size.Y:F0}) " +
               $"nav=({navMin.Position.X:F0},{navMin.Position.Y:F0}) drawerVisible={_drawerPanel.Visible} inspVisible={_inspector.Visible}";
    }

    private void BuildLeftNav(Control root, string lang)
    {
        var rail = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 6);
        rail.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        rail.OffsetLeft = 8;
        rail.OffsetTop = 76;
        rail.OffsetRight = 104;
        rail.MouseFilter = Control.MouseFilterEnum.Stop;
        root.AddChild(rail);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        rail.AddChild(col);

        AddNavBtn(col, "Realm", _loc.Get(lang, "tab.realm"));
        AddNavBtn(col, "Markets", _loc.Get(lang, "tab.markets"));
        AddNavBtn(col, "Pops", _loc.Get(lang, "tab.pops"));
    }

    private void AddNavBtn(VBoxContainer parent, string tabId, string text)
    {
        var btn = UiThemeFactory.CreateStyledButton(text, 84, 34);
        btn.Pressed += () => ToggleLeftTab(tabId);
        _navButtons[tabId] = btn;
        parent.AddChild(btn);
    }

    private void BuildDrawer(Control root)
    {
        _drawerPanel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.BronzeAccent, 10);
        _drawerPanel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _drawerPanel.OffsetLeft = 112;
        _drawerPanel.OffsetTop = 76;
        _drawerPanel.OffsetRight = 560;
        _drawerPanel.OffsetBottom = -64;
        _drawerPanel.Visible = false;
        _drawerPanel.MouseFilter = Control.MouseFilterEnum.Stop;
        root.AddChild(_drawerPanel);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        _drawerPanel.AddChild(v);

        var head = new HBoxContainer();
        _drawerTitle = UiThemeFactory.CreateHeader("Drawer", 15);
        _drawerTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(_drawerTitle);

        var closeBtn = UiThemeFactory.CreateStyledButton("X", 28, 26);
        closeBtn.Pressed += () => QaOpenTab("None");
        head.AddChild(closeBtn);
        v.AddChild(head);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _drawerContent = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _drawerContent.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_drawerContent);
        v.AddChild(scroll);
    }

    private void BuildOutliner(Control root, string lang)
    {
        _outlinerPanel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 8);
        _outlinerPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _outlinerPanel.OffsetLeft = -270;
        _outlinerPanel.OffsetRight = -10;
        _outlinerPanel.OffsetTop = 76;
        _outlinerPanel.OffsetBottom = 268;
        _outlinerPanel.MouseFilter = Control.MouseFilterEnum.Stop;
        root.AddChild(_outlinerPanel);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        _outlinerPanel.AddChild(v);

        _outlinerTitle = UiThemeFactory.CreateHeader(_loc.Get(lang, "outliner.title"), 13);
        v.AddChild(_outlinerTitle);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _outlinerContent = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _outlinerContent.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_outlinerContent);
        v.AddChild(scroll);
    }

    private void ToggleLeftTab(string tab)
    {
        if (_activeTab == tab && _drawerPanel.Visible)
        {
            QaOpenTab("None");
            return;
        }

        _activeTab = tab;
        _drawerPanel.Visible = true;
        foreach (var kv in _navButtons)
            UiThemeFactory.ApplyButtonHighlight(kv.Value, kv.Key == tab);
        RefreshDrawerContent();
    }

    private void RefreshDrawerContent()
    {
        if (_latestSnapshot is null)
            return;

        var lang = _settings.ResolveActiveLanguage();
        UiThemeFactory.ClearChildren(_drawerContent);

        switch (_activeTab)
        {
            case "Realm":
                _drawerTitle.Text = _loc.Get(lang, "drawer.realm_title");
                _realmDrawer.Render(_drawerContent, _latestSnapshot, lang, cmd => CommandIssued?.Invoke(cmd));
                break;
            case "Markets":
                _drawerTitle.Text = _loc.Get(lang, "drawer.market_title");
                _marketDrawer.Render(_drawerContent, _latestSnapshot, lang, RefreshDrawerContent);
                break;
            case "Pops":
                _drawerTitle.Text = _loc.Get(lang, "drawer.pops_title");
                _popsDrawer.Render(_drawerContent, _latestSnapshot, lang);
                break;
        }
    }

    private void RefreshOutliner(RenderSnapshot snap)
    {
        UiThemeFactory.ClearChildren(_outlinerContent);
        foreach (var st in snap.States)
        {
            if (st.OwnerNationId != snap.Realm.NationId)
                continue;
            var sId = st.StateId;
            var btn = UiThemeFactory.CreateStyledButton(
                $"{st.Name}  ({st.AverageControl:F0}% · {st.EffectiveTaxBase:F0}tb)",
                220,
                24);
            btn.Pressed += () => FocusStateRequested?.Invoke(sId);
            _outlinerContent.AddChild(btn);
        }
    }
}
