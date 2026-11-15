using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

/// <summary>
/// Full CK3 + EU4 inspired Grand Strategy HUD built 100% code-first over MapView2D:
/// - Top Resource Bar: Nation Crest Shield, structured resource pills with clickable nested tooltips, and Imperial Chronometer
/// - CK3 "Situacao Atual" contextual alert strip (auto-routes directly to the matching drawer)
/// - Left Navigation Rail + Scrollable Drawers with Clout / Tech / Law / WarScore / Journal progress bars
/// - Collapsible Right Outliner OR EU4-inspired State & Province Inspector (Terrain banner, Cultural Admin Tier, Gauges)
/// - Bottom-Left CK3 Ruler Portrait, Dual Ancestry Pills & Live Stress Gauge
/// - Hover Banner, Highlighted Bottom Map Modes Bar, Nested Tooltips, and Pause Menu
/// </summary>
public partial class HudRoot : CanvasLayer
{
    public enum ActiveDrawerTab { None, Council, Market, Pops, Politics, Technology, Diplomacy, Journal }

    private readonly LocalizationCatalog _loc;
    private GameSettings _settings;
    private RenderSnapshot? _latestSnapshot;
    private string? _selectedStateId;
    private string? _selectedProvinceId;
    private ProvinceSnapshot? _hoveredProvince;
    private ActiveDrawerTab _activeTab = ActiveDrawerTab.None;
    private MapLensMode _currentLens = MapLensMode.Political;
    private bool _outlinerCollapsed;
    private string _lastRenderedAncestry = string.Empty;

    private Control _hudRoot = null!;

    // Top Bar (EU4 Coat-of-Arms + Resource Pills + Chronometer)
    private PanelContainer _topBarPanel = null!;
    private Button _nationCrestBtn = null!;
    private ColorRect _nationCrestColor = null!;
    private Label _nationTagLabel = null!;
    private Label _nationNameLabel = null!;
    private Button _treasuryPillBtn = null!;
    private Label _bureaucracyPillLabel = null!;
    private Label _authorityPillLabel = null!;
    private Button _stressPillBtn = null!;
    private Label _populationPillLabel = null!;
    private Label _dateLabel = null!;
    private Button _pauseBtn = null!;
    private readonly Button[] _speedButtons = new Button[5];
    private Button _debugBadgeBtn = null!;
    private Button _menuBtn = null!;

    // CK3 Current Situation Contextual Alert Strip
    private PanelContainer _alertStrip = null!;
    private HBoxContainer _alertRow = null!;

    // Left Navigation Rail & Drawer
    private VBoxContainer _leftNavBox = null!;
    private readonly Dictionary<ActiveDrawerTab, Button> _tabButtons = new();
    private PanelContainer _leftDrawer = null!;
    private Label _leftDrawerTitle = null!;
    private Button _leftDrawerCloseBtn = null!;
    private VBoxContainer _leftDrawerBody = null!;

    // Right Outliner & State+Province Inspector (mutually exclusive)
    private PanelContainer _outlinerPanel = null!;
    private Label _outlinerTitle = null!;
    private Button _outlinerCollapseBtn = null!;
    private ScrollContainer _outlinerScroll = null!;
    private VBoxContainer _outlinerBody = null!;

    private PanelContainer _stateDrawer = null!;
    private Label _stateTitleLabel = null!;
    private Label _stateMetaLabel = null!;
    private Button _stateFocusBtn = null!;
    private Button _stateCloseBtn = null!;
    private HFlowContainer _provinceSelectorFlow = null!;
    private PanelContainer _activeProvinceCard = null!;
    private VBoxContainer _activeProvinceBody = null!;
    private VBoxContainer _stateBuildingsBox = null!;

    // Bottom-Left CK3 Ruler Portrait, Dual Ancestry & Stress Box
    private PanelContainer _dynastyPanel = null!;
    private ColorRect _portraitCrestFill = null!;
    private Label _portraitMonogramLabel = null!;
    private Label _dynastyRulerLabel = null!;
    private HFlowContainer _ancestryPillsFlow = null!;
    private Label _dynastyHeirLabel = null!;
    private ProgressBar _dynastyStressBar = null!;
    private Label _dynastyStressValLabel = null!;
    private Button _dynastyTooltipBtn = null!;

    // Bottom-Center Map Lenses & Hover Strip
    private PanelContainer _hoverBanner = null!;
    private ColorRect _hoverNationDot = null!;
    private Label _hoverBannerLabel = null!;
    private readonly Dictionary<MapLensMode, Button> _lensButtons = new();

    // Pause Overlay & Nested Tooltip Modal
    private ColorRect _pauseOverlay = null!;
    private Label _pauseTitle = null!;
    private Label _pauseStatusLabel = null!;
    private Button _resumeBtn = null!;
    private Button _quicksaveBtn = null!;
    private Button _pauseSettingsBtn = null!;
    private Button _toMenuBtn = null!;
    private SettingsModal _settingsModal = null!;
    private NestedTooltipManager _tooltip = null!;
    private DebugOverlayPanel _debugPanel = null!;

    public event Action<GameCommand>? CommandIssued;
    public event Action<MapLensMode>? LensModeChanged;
    public event Action<string>? FocusStateRequested;
    public event Action? QuicksaveRequested;
    public event Action? ReturnToMainMenuRequested;
    public event Action<GameSettings>? SettingsChanged;
    public event Action<int>? DebugFastForwardDaysRequested;
    public event Action? DebugBenchmarkSpeedsRequested;
    public event Action? DebugAuditWorldRequested;

    public bool IsPauseMenuOpen => _pauseOverlay.Visible;
    public bool IsDebugOverlayVisible => _settings.DebugMode && _debugPanel.Visible;
    public string? SelectedStateId => _selectedStateId;
    public string? SelectedProvinceId => _selectedProvinceId;

    public HudRoot(LocalizationCatalog loc, GameSettings initialSettings)
    {
        _loc = loc;
        _settings = initialSettings;
        Layer = 10;
    }

    public override void _Ready()
    {
        _hudRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hudRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_hudRoot);

        BuildTopBar();
        BuildAlertStrip();
        BuildLeftNavAndDrawer();
        BuildOutliner();
        BuildStateInspector();
        BuildDynastyCard();
        BuildHoverBannerAndBottomLenses();
        BuildPauseMenu();

        _debugPanel = new DebugOverlayPanel(_loc, _settings) { Visible = _settings.DebugMode };
        _debugPanel.FastForwardDaysRequested += days => DebugFastForwardDaysRequested?.Invoke(days);
        _debugPanel.BenchmarkSpeedsRequested += () => DebugBenchmarkSpeedsRequested?.Invoke();
        _debugPanel.AuditWorldRequested += () => DebugAuditWorldRequested?.Invoke();
        _debugPanel.CollapseRequested += () => _debugPanel.Visible = false;
        _hudRoot.AddChild(_debugPanel);

        _settingsModal = new SettingsModal(_loc, _settings) { Visible = false };
        _settingsModal.SettingsApplied += s =>
        {
            var wasDebug = _settings.DebugMode;
            _settings = s;
            SettingsChanged?.Invoke(s);
            if (!wasDebug && s.DebugMode)
                _debugPanel.Visible = true;
            else if (!s.DebugMode)
                _debugPanel.Visible = false;
            ApplyLocalization();
            RefreshCurrentViews();
        };
        AddChild(_settingsModal);

        _tooltip = new NestedTooltipManager();
        AddChild(_tooltip);

        ApplyLocalization();
        UpdateTabAndLensHighlights();
    }

    public void SetSettings(GameSettings settings)
    {
        var wasDebug = _settings.DebugMode;
        _settings = settings;
        if (_debugPanel is not null)
        {
            _debugPanel.ApplySettings(settings);
            if (!wasDebug && settings.DebugMode)
                _debugPanel.Visible = true;
            else if (!settings.DebugMode)
                _debugPanel.Visible = false;
        }
        ApplyLocalization();
        RefreshCurrentViews();
    }

    public void ToggleDebugOverlay()
    {
        if (!_settings.DebugMode || _debugPanel is null)
            return;
        _debugPanel.Visible = !_debugPanel.Visible;
    }

    public void SetDebugOverlayVisible(bool visible)
    {
        if (_debugPanel is null)
            return;
        _debugPanel.Visible = _settings.DebugMode && visible;
    }

    public void UpdateClockOnly(GameDate date, bool isPaused, int speed)
    {
        var l = _settings.ResolveActiveLanguage();
        _dateLabel.Text = date.ToDisplayString();
        _pauseBtn.Text = isPaused ? _loc.Get(l, "hud.play") : _loc.Get(l, "hud.pause");
        UiThemeFactory.ApplyButtonHighlight(_pauseBtn, isPaused, UiThemeFactory.BronzeAccent);
        for (var i = 0; i < 5; i++)
            UiThemeFactory.ApplyButtonHighlight(_speedButtons[i], speed == i + 1, UiThemeFactory.SteelBorder);
    }

    public void UpdateDebugTelemetry(in SimTelemetryMetrics metrics, RenderSnapshot? snapshot, SimulationIntegrityReport? integrity)
    {
        if (!_settings.DebugMode || _debugPanel is null)
            return;
        var focus = _hoveredProvince;
        if (focus is null && snapshot is not null && _selectedProvinceId is not null)
        {
            foreach (var p in snapshot.Provinces)
            {
                if (p.ProvinceId == _selectedProvinceId)
                {
                    focus = p;
                    break;
                }
            }
        }
        _debugPanel.UpdateTelemetry(metrics, snapshot ?? _latestSnapshot, integrity, focus);
    }

    public void SetDebugReportText(string text)
    {
        _debugPanel?.SetReportText(text);
    }

    public void SetActiveLens(MapLensMode mode)
    {
        _currentLens = mode;
        UpdateTabAndLensHighlights();
    }

    public void TogglePauseMenu()
    {
        var open = !_pauseOverlay.Visible;
        _pauseOverlay.Visible = open;
        _pauseStatusLabel.Text = string.Empty;
        if (open)
            CommandIssued?.Invoke(new SetPausedCommand(true));
    }

    public void NotifySaveSucceeded()
    {
        var lang = _settings.ResolveActiveLanguage();
        _pauseStatusLabel.Text = _loc.Get(lang, "pause.saved_ok");
    }

    public void UpdateFromSnapshot(RenderSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        RefreshTopBarAndDynasty(snapshot);
        RefreshAlertStrip(snapshot);
        RefreshOutliner(snapshot);

        if (_activeTab != ActiveDrawerTab.None)
            RenderActiveDrawerTab();
        if (_stateDrawer.Visible && _selectedStateId is not null)
            RenderStateInspector(_selectedStateId);
    }

    public void SelectProvince(string provinceId, string stateId)
    {
        _selectedProvinceId = provinceId;
        _selectedStateId = stateId;
        _stateDrawer.Visible = true;
        _outlinerPanel.Visible = false;
        RenderStateInspector(stateId);
    }

    public void SelectState(string stateId)
    {
        _selectedStateId = stateId;
        if (_latestSnapshot is not null)
        {
            var capOrFirst = _latestSnapshot.Provinces.FirstOrDefault(p => p.StateId == stateId && p.IsCapital)
                          ?? _latestSnapshot.Provinces.FirstOrDefault(p => p.StateId == stateId);
            if (capOrFirst is not null)
                _selectedProvinceId = capOrFirst.ProvinceId;
        }
        _stateDrawer.Visible = true;
        _outlinerPanel.Visible = false;
        RenderStateInspector(stateId);
    }

    public void CloseStateInspector()
    {
        _stateDrawer.Visible = false;
        _outlinerPanel.Visible = true;
    }

    public void SetHoverProvince(ProvinceSnapshot? prov)
    {
        _hoveredProvince = prov;
        if (prov is null || _latestSnapshot is null)
        {
            _hoverBanner.Visible = false;
            return;
        }

        var lang = _settings.ResolveActiveLanguage();
        var state = _latestSnapshot.States.FirstOrDefault(s => s.StateId == prov.StateId);
        var terrainName = _loc.Get(lang, $"terrain.{prov.Terrain}");
        var hubName = _loc.Get(lang, $"hub.{prov.HubType}");
        var ownerTag = prov.OccupiedByNationId is not null
            ? $"{prov.OwnerNationId} ({_loc.Get(lang, "province.occupied_by")} {prov.OccupiedByNationId})"
            : prov.OwnerNationId;

        _hoverNationDot.Color = UiThemeFactory.ParseHexColor(state?.OwnerColorHex, UiThemeFactory.GoldAccent);
        var displayName = !string.IsNullOrWhiteSpace(prov.DynamicDisplayName) ? prov.DynamicDisplayName : prov.Name;
        _hoverBannerLabel.Text = $"{displayName} [{ownerTag}]  |  {state?.Name ?? prov.StateId}  |  {terrainName}  •  {hubName} Lv.{prov.HoldingLevel}"
            + (prov.FortLevel > 0 ? $"  •  Fort {prov.FortLevel}" : string.Empty);
        _hoverBanner.Visible = true;
    }

    internal void QaOpenTab(string tabName)
    {
        if (Enum.TryParse<ActiveDrawerTab>(tabName, out var parsed))
        {
            _activeTab = parsed;
            _leftDrawer.Visible = parsed != ActiveDrawerTab.None;
            UpdateTabAndLensHighlights();
            if (_latestSnapshot is not null)
                RefreshAlertStrip(_latestSnapshot);
            if (_leftDrawer.Visible)
                RenderActiveDrawerTab();
        }
    }

    public string InspectLayoutBounds()
    {
        var vpSize = _hudRoot.GetViewportRect().Size;
        var issues = new List<string>();

        CheckControlBounds("TopMenuBtn", _menuBtn, vpSize, issues);
        CheckControlBounds("TopBar", _topBarPanel, vpSize, issues);
        CheckControlBounds("DynastyPanel", _dynastyPanel, vpSize, issues);
        if (_alertStrip.Visible)
            CheckControlBounds("AlertStrip", _alertStrip, vpSize, issues);
        if (_leftDrawer.Visible)
            CheckControlBounds("LeftDrawer", _leftDrawer, vpSize, issues);
        if (_leftDrawer.Visible && _dynastyPanel.GetGlobalRect().Intersects(_leftDrawer.GetGlobalRect()))
            issues.Add("OVERLAP(Dynasty,LeftDrawer)");
        if (_outlinerPanel.Visible)
            CheckControlBounds("Outliner", _outlinerPanel, vpSize, issues);
        if (_stateDrawer.Visible)
            CheckControlBounds("StateInspector", _stateDrawer, vpSize, issues);
        if (_debugPanel.Visible)
            CheckControlBounds("DebugPanel", _debugPanel, vpSize, issues);

        return issues.Count == 0 ? "bounds=OK" : $"bounds=WARN[{string.Join(",", issues)}]";
    }

    private static void CheckControlBounds(string name, Control ctrl, Vector2 vpSize, List<string> issues)
    {
        var r = ctrl.GetGlobalRect();
        if (r.Position.X < -1 || r.Position.Y < -1 || r.End.X > vpSize.X + 1 || r.End.Y > vpSize.Y + 1)
            issues.Add($"CLIP({name}:{r.Position.X:F0},{r.Position.Y:F0}->{r.End.X:F0},{r.End.Y:F0})");
    }

    private void ApplyLocalization()
    {
        var l = _settings.ResolveActiveLanguage();
        _tooltip?.SetLanguage(l);
        _pauseBtn.Text = _loc.Get(l, "hud.pause");
        _debugBadgeBtn.Text = _loc.Get(l, "debug.badge");
        _debugBadgeBtn.Visible = _settings.DebugMode;
        _menuBtn.Text = _loc.Get(l, "hud.menu_btn");
        _dynastyTooltipBtn.Text = _loc.Get(l, "hud.tooltip_rule");
        _leftDrawerCloseBtn.Text = _loc.Get(l, "drawer.close");
        _stateCloseBtn.Text = _loc.Get(l, "drawer.close");
        _outlinerTitle.Text = _loc.Get(l, "outliner.title");

        _tabButtons[ActiveDrawerTab.Council].Text = _loc.Get(l, "tab.council");
        _tabButtons[ActiveDrawerTab.Market].Text = _loc.Get(l, "tab.market");
        _tabButtons[ActiveDrawerTab.Pops].Text = _loc.Get(l, "tab.pops");
        _tabButtons[ActiveDrawerTab.Politics].Text = _loc.Get(l, "tab.politics");
        _tabButtons[ActiveDrawerTab.Technology].Text = _loc.Get(l, "tab.tech");
        _tabButtons[ActiveDrawerTab.Diplomacy].Text = _loc.Get(l, "tab.diplomacy");
        _tabButtons[ActiveDrawerTab.Journal].Text = _loc.Get(l, "tab.journal");

        _lensButtons[MapLensMode.Political].Text = $"[1] {_loc.Get(l, "lens.political")}";
        _lensButtons[MapLensMode.Economic].Text = $"[2] {_loc.Get(l, "lens.economic")}";
        _lensButtons[MapLensMode.Ancestry].Text = $"[3] {_loc.Get(l, "lens.ancestry")}";
        _lensButtons[MapLensMode.StandardOfLiving].Text = $"[4] {_loc.Get(l, "lens.sol")}";
        _lensButtons[MapLensMode.Diplomacy].Text = $"[5] {_loc.Get(l, "lens.diplomacy")}";

        _pauseTitle.Text = _loc.Get(l, "pause.title");
        _resumeBtn.Text = _loc.Get(l, "pause.resume");
        _quicksaveBtn.Text = _loc.Get(l, "pause.save");
        _pauseSettingsBtn.Text = _loc.Get(l, "pause.settings");
        _toMenuBtn.Text = _loc.Get(l, "pause.to_menu");
    }

    private void RefreshCurrentViews()
    {
        if (_latestSnapshot is not null)
            UpdateFromSnapshot(_latestSnapshot);
        if (_hoveredProvince is not null)
            SetHoverProvince(_hoveredProvince);
    }

    private void BuildTopBar()
    {
        _topBarPanel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 6);
        _topBarPanel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _topBarPanel.OffsetBottom = 52;
        _hudRoot.AddChild(_topBarPanel);

        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);
        _topBarPanel.AddChild(row);

        // EU4-inspired National Heraldic Shield + Name Pill
        var crestPill = UiThemeFactory.CreatePill(UiThemeFactory.HeaderBg, UiThemeFactory.GoldAccent, 6, 3);
        row.AddChild(crestPill);

        var crestRow = new HBoxContainer();
        crestRow.AddThemeConstantOverride("separation", 6);
        crestPill.AddChild(crestRow);

        _nationCrestColor = new ColorRect
        {
            CustomMinimumSize = new Vector2(18, 24),
            Color = UiThemeFactory.GoldAccent
        };
        crestRow.AddChild(_nationCrestColor);

        _nationCrestBtn = new Button
        {
            Flat = true,
            CustomMinimumSize = new Vector2(210, 26),
            Alignment = HorizontalAlignment.Left
        };
        _nationCrestBtn.Pressed += () => ToggleLeftTab(ActiveDrawerTab.Council);
        crestRow.AddChild(_nationCrestBtn);

        var crestTextRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        crestTextRow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        crestTextRow.AddThemeConstantOverride("separation", 5);
        _nationCrestBtn.AddChild(crestTextRow);

        _nationTagLabel = UiThemeFactory.CreateHeader("AET", 12, UiThemeFactory.GoldAccent);
        _nationTagLabel.VerticalAlignment = VerticalAlignment.Center;
        crestTextRow.AddChild(_nationTagLabel);

        _nationNameLabel = new Label
        {
            Text = "Aethelgard",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        _nationNameLabel.AddThemeFontSizeOverride("font_size", 13);
        crestTextRow.AddChild(_nationNameLabel);

        // Structured Resource Metric Pills (Treasury + Stress open Nested Tooltips)
        var metricsRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        metricsRow.AddThemeConstantOverride("separation", 5);
        row.AddChild(metricsRow);

        _treasuryPillBtn = UiThemeFactory.CreateStyledButton("0 Ouro (+0)", 135, 30, UiThemeFactory.BronzeAccent);
        _treasuryPillBtn.Pressed += () =>
            _tooltip.ShowConcept("treasury", 0, _treasuryPillBtn.GetGlobalRect().Position + new Vector2(0, 34));
        metricsRow.AddChild(_treasuryPillBtn);

        var burPill = UiThemeFactory.CreatePill(UiThemeFactory.PillBg, UiThemeFactory.SteelBorder, 8, 3);
        _bureaucracyPillLabel = CreateMetricLabel("Bur +0", UiThemeFactory.CyanAether);
        burPill.AddChild(_bureaucracyPillLabel);
        metricsRow.AddChild(burPill);

        var authPill = UiThemeFactory.CreatePill(UiThemeFactory.PillBg, UiThemeFactory.SteelBorder, 8, 3);
        _authorityPillLabel = CreateMetricLabel("Aut 0", UiThemeFactory.GoldAccent);
        authPill.AddChild(_authorityPillLabel);
        metricsRow.AddChild(authPill);

        _stressPillBtn = UiThemeFactory.CreateStyledButton("Est 0/300", 88, 30, UiThemeFactory.SteelBorder);
        _stressPillBtn.Pressed += () =>
            _tooltip.ShowConcept("stress", 0, _stressPillBtn.GetGlobalRect().Position + new Vector2(0, 34));
        metricsRow.AddChild(_stressPillBtn);

        var popPill = UiThemeFactory.CreatePill(UiThemeFactory.PillBg, UiThemeFactory.SteelBorder, 8, 3);
        _populationPillLabel = CreateMetricLabel("Pop 0", Colors.White);
        popPill.AddChild(_populationPillLabel);
        metricsRow.AddChild(popPill);

        // Right Imperial Chronometer & Speed Block
        var clockPill = UiThemeFactory.CreatePill(UiThemeFactory.HeaderBg, UiThemeFactory.BronzeAccent, 8, 3);
        row.AddChild(clockPill);

        var clockRow = new HBoxContainer();
        clockRow.AddThemeConstantOverride("separation", 4);
        clockPill.AddChild(clockRow);

        _dateLabel = UiThemeFactory.CreateHeader("1380 D.Q.", 13, UiThemeFactory.GoldAccent);
        _dateLabel.CustomMinimumSize = new Vector2(106, 0);
        _dateLabel.VerticalAlignment = VerticalAlignment.Center;
        clockRow.AddChild(_dateLabel);

        _pauseBtn = UiThemeFactory.CreateStyledButton("||", 64, 28, UiThemeFactory.BronzeAccent);
        _pauseBtn.Pressed += () =>
        {
            if (_latestSnapshot is not null)
                CommandIssued?.Invoke(new SetPausedCommand(!_latestSnapshot.IsPaused));
        };
        clockRow.AddChild(_pauseBtn);

        for (var i = 0; i < 5; i++)
        {
            var spd = i + 1;
            var btn = UiThemeFactory.CreateStyledButton($"{spd}x", 28, 28, UiThemeFactory.SteelBorder);
            btn.Pressed += () => CommandIssued?.Invoke(new SetSpeedCommand(spd));
            _speedButtons[i] = btn;
            clockRow.AddChild(btn);
        }

        _debugBadgeBtn = UiThemeFactory.CreateStyledButton("Debug [F3]", 86, 30, UiThemeFactory.CyanAether);
        _debugBadgeBtn.Visible = _settings.DebugMode;
        _debugBadgeBtn.Pressed += ToggleDebugOverlay;
        row.AddChild(_debugBadgeBtn);

        _menuBtn = UiThemeFactory.CreateStyledButton("Menu", 68, 30, UiThemeFactory.SteelBorder);
        _menuBtn.Pressed += TogglePauseMenu;
        row.AddChild(_menuBtn);
    }

    private static Label CreateMetricLabel(string text, Color color)
    {
        var lbl = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }

    private void BuildAlertStrip()
    {
        _alertStrip = UiThemeFactory.CreatePill(new Color(0.08f, 0.08f, 0.10f, 0.90f), UiThemeFactory.BronzeAccent, 6, 3);
        _alertStrip.Visible = false;
        _alertStrip.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _alertStrip.OffsetLeft = -245;
        _alertStrip.OffsetRight = 245;
        _alertStrip.OffsetTop = 58;
        _alertStrip.OffsetBottom = 90;
        _hudRoot.AddChild(_alertStrip);

        _alertRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _alertRow.AddThemeConstantOverride("separation", 6);
        _alertStrip.AddChild(_alertRow);
    }

    private void BuildLeftNavAndDrawer()
    {
        _leftNavBox = new VBoxContainer { OffsetLeft = 10, OffsetTop = 62, OffsetRight = 142, OffsetBottom = 380 };
        _leftNavBox.AddThemeConstantOverride("separation", 5);
        _hudRoot.AddChild(_leftNavBox);

        var tabs = new[]
        {
            ActiveDrawerTab.Council,
            ActiveDrawerTab.Market,
            ActiveDrawerTab.Pops,
            ActiveDrawerTab.Politics,
            ActiveDrawerTab.Technology,
            ActiveDrawerTab.Diplomacy,
            ActiveDrawerTab.Journal
        };

        foreach (var tab in tabs)
        {
            var t = tab;
            var btn = UiThemeFactory.CreateStyledButton(string.Empty, 132, 34, UiThemeFactory.SteelBorder);
            btn.Alignment = HorizontalAlignment.Left;
            btn.Pressed += () => ToggleLeftTab(t);
            _tabButtons[t] = btn;
            _leftNavBox.AddChild(btn);
        }

        _leftDrawer = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 12);
        _leftDrawer.Visible = false;
        _leftDrawer.AnchorTop = 0;
        _leftDrawer.AnchorBottom = 1;
        _leftDrawer.OffsetLeft = 150;
        _leftDrawer.OffsetRight = 568;
        _leftDrawer.OffsetTop = 62;
        _leftDrawer.OffsetBottom = -152;
        _hudRoot.AddChild(_leftDrawer);

        var v = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 8);
        _leftDrawer.AddChild(v);

        var hdr = new HBoxContainer();
        _leftDrawerTitle = UiThemeFactory.CreateHeader(string.Empty, 17, UiThemeFactory.GoldAccent);
        _leftDrawerTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hdr.AddChild(_leftDrawerTitle);
        _leftDrawerCloseBtn = UiThemeFactory.CreateStyledButton("Close", 74, 30, UiThemeFactory.SteelBorder);
        _leftDrawerCloseBtn.Pressed += () =>
        {
            _activeTab = ActiveDrawerTab.None;
            _leftDrawer.Visible = false;
            UpdateTabAndLensHighlights();
            if (_latestSnapshot is not null)
                RefreshAlertStrip(_latestSnapshot);
        };
        hdr.AddChild(_leftDrawerCloseBtn);
        v.AddChild(hdr);
        v.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _leftDrawerBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _leftDrawerBody.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_leftDrawerBody);
        v.AddChild(scroll);
    }

    private void BuildOutliner()
    {
        _outlinerPanel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 10);
        _outlinerPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _outlinerPanel.OffsetLeft = -316;
        _outlinerPanel.OffsetTop = 62;
        _outlinerPanel.OffsetRight = -10;
        _outlinerPanel.OffsetBottom = 368;
        _hudRoot.AddChild(_outlinerPanel);

        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 5);
        _outlinerPanel.AddChild(v);

        var hdr = new HBoxContainer();
        _outlinerTitle = UiThemeFactory.CreateHeader("Resumo", 14, UiThemeFactory.GoldAccent);
        _outlinerTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hdr.AddChild(_outlinerTitle);

        _outlinerCollapseBtn = UiThemeFactory.CreateStyledButton("−", 26, 22, UiThemeFactory.SteelBorder);
        _outlinerCollapseBtn.Pressed += ToggleOutlinerCollapse;
        hdr.AddChild(_outlinerCollapseBtn);
        v.AddChild(hdr);
        v.AddChild(new HSeparator());

        _outlinerScroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _outlinerBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _outlinerBody.AddThemeConstantOverride("separation", 6);
        _outlinerScroll.AddChild(_outlinerBody);
        v.AddChild(_outlinerScroll);
    }

    private void ToggleOutlinerCollapse()
    {
        _outlinerCollapsed = !_outlinerCollapsed;
        _outlinerScroll.Visible = !_outlinerCollapsed;
        _outlinerCollapseBtn.Text = _outlinerCollapsed ? "+" : "−";
        _outlinerPanel.OffsetBottom = _outlinerCollapsed ? 106 : 368;
    }

    private void BuildStateInspector()
    {
        _stateDrawer = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 12);
        _stateDrawer.Visible = false;
        _stateDrawer.AnchorLeft = 1;
        _stateDrawer.AnchorRight = 1;
        _stateDrawer.AnchorTop = 0;
        _stateDrawer.AnchorBottom = 1;
        _stateDrawer.OffsetLeft = -476;
        _stateDrawer.OffsetRight = -10;
        _stateDrawer.OffsetTop = 62;
        _stateDrawer.OffsetBottom = -84;
        _hudRoot.AddChild(_stateDrawer);

        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 8);
        _stateDrawer.AddChild(v);

        var hdr = new HBoxContainer();
        hdr.AddThemeConstantOverride("separation", 6);
        _stateTitleLabel = UiThemeFactory.CreateHeader(string.Empty, 17, UiThemeFactory.GoldAccent);
        _stateTitleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _stateTitleLabel.ClipText = true;
        hdr.AddChild(_stateTitleLabel);

        _stateFocusBtn = UiThemeFactory.CreateStyledButton("◎", 32, 30, UiThemeFactory.CyanAether);
        _stateFocusBtn.Pressed += () => { if (_selectedStateId is not null) FocusStateRequested?.Invoke(_selectedStateId); };
        hdr.AddChild(_stateFocusBtn);

        _stateCloseBtn = UiThemeFactory.CreateStyledButton("Close", 74, 30, UiThemeFactory.SteelBorder);
        _stateCloseBtn.Pressed += CloseStateInspector;
        hdr.AddChild(_stateCloseBtn);
        v.AddChild(hdr);

        _stateMetaLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _stateMetaLabel.AddThemeFontSizeOverride("font_size", 12);
        _stateMetaLabel.AddThemeColorOverride("font_color", UiThemeFactory.MutedText);
        v.AddChild(_stateMetaLabel);
        v.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        var scrollInner = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scrollInner.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(scrollInner);
        v.AddChild(scroll);

        _provinceSelectorFlow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _provinceSelectorFlow.AddThemeConstantOverride("h_separation", 6);
        _provinceSelectorFlow.AddThemeConstantOverride("v_separation", 6);
        scrollInner.AddChild(_provinceSelectorFlow);

        _activeProvinceCard = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, UiThemeFactory.GoldAccent, 10);
        _activeProvinceBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _activeProvinceBody.AddThemeConstantOverride("separation", 6);
        _activeProvinceCard.AddChild(_activeProvinceBody);
        scrollInner.AddChild(_activeProvinceCard);

        _stateBuildingsBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _stateBuildingsBox.AddThemeConstantOverride("separation", 8);
        scrollInner.AddChild(_stateBuildingsBox);
    }

    private void BuildDynastyCard()
    {
        _dynastyPanel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.BronzeAccent, 9);
        _dynastyPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _dynastyPanel.OffsetLeft = 10;
        _dynastyPanel.OffsetTop = -144;
        _dynastyPanel.OffsetRight = 384;
        _dynastyPanel.OffsetBottom = -10;
        _hudRoot.AddChild(_dynastyPanel);

        var outer = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        outer.AddThemeConstantOverride("separation", 5);
        _dynastyPanel.AddChild(outer);

        var topRow = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        topRow.AddThemeConstantOverride("separation", 8);
        outer.AddChild(topRow);

        // CK3-inspired Ruler Heraldic Medallion / Portrait Crest
        var portraitFrame = UiThemeFactory.CreatePanel(UiThemeFactory.HeaderBg, UiThemeFactory.GoldAccent, 3);
        portraitFrame.CustomMinimumSize = new Vector2(54, 62);
        topRow.AddChild(portraitFrame);

        _portraitCrestFill = new ColorRect
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Color = new Color(0.28f, 0.22f, 0.16f)
        };
        portraitFrame.AddChild(_portraitCrestFill);

        _portraitMonogramLabel = new Label
        {
            Text = "★",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _portraitMonogramLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _portraitMonogramLabel.AddThemeFontSizeOverride("font_size", 20);
        _portraitMonogramLabel.AddThemeColorOverride("font_color", UiThemeFactory.GoldAccent);
        portraitFrame.AddChild(_portraitMonogramLabel);

        var infoCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        infoCol.AddThemeConstantOverride("separation", 3);
        topRow.AddChild(infoCol);

        _dynastyRulerLabel = UiThemeFactory.CreateHeader("Ruler", 13, UiThemeFactory.GoldAccent);
        _dynastyRulerLabel.ClipText = true;
        infoCol.AddChild(_dynastyRulerLabel);

        _ancestryPillsFlow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _ancestryPillsFlow.AddThemeConstantOverride("h_separation", 4);
        _ancestryPillsFlow.AddThemeConstantOverride("v_separation", 2);
        infoCol.AddChild(_ancestryPillsFlow);

        _dynastyHeirLabel = new Label { ClipText = true };
        _dynastyHeirLabel.AddThemeFontSizeOverride("font_size", 11);
        _dynastyHeirLabel.AddThemeColorOverride("font_color", UiThemeFactory.MutedText);
        infoCol.AddChild(_dynastyHeirLabel);

        var stressRow = new HBoxContainer();
        stressRow.AddThemeConstantOverride("separation", 6);
        infoCol.AddChild(stressRow);

        _dynastyStressBar = UiThemeFactory.CreateStyledProgressBar(UiThemeFactory.EmeraldPositive, 300f, 0f, 8f);
        _dynastyStressBar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        stressRow.AddChild(_dynastyStressBar);

        _dynastyStressValLabel = new Label { Text = "0/300" };
        _dynastyStressValLabel.AddThemeFontSizeOverride("font_size", 11);
        stressRow.AddChild(_dynastyStressValLabel);

        _dynastyTooltipBtn = UiThemeFactory.CreateStyledButton(string.Empty, 0, 26, UiThemeFactory.CyanAether);
        _dynastyTooltipBtn.Pressed += () =>
            _tooltip.ShowConcept("dual_ancestry", 0, _dynastyTooltipBtn.GetGlobalRect().Position);
        outer.AddChild(_dynastyTooltipBtn);
    }

    private void BuildHoverBannerAndBottomLenses()
    {
        _hoverBanner = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.CyanAether, 6);
        _hoverBanner.Visible = false;
        _hoverBanner.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _hoverBanner.OffsetLeft = -430;
        _hoverBanner.OffsetRight = 430;
        _hoverBanner.OffsetTop = -102;
        _hoverBanner.OffsetBottom = -66;
        _hoverBanner.MouseFilter = Control.MouseFilterEnum.Ignore;
        _hudRoot.AddChild(_hoverBanner);

        var hRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hRow.AddThemeConstantOverride("separation", 8);
        _hoverBanner.AddChild(hRow);

        _hoverNationDot = new ColorRect { CustomMinimumSize = new Vector2(10, 14), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        hRow.AddChild(_hoverNationDot);

        _hoverBannerLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true
        };
        _hoverBannerLabel.AddThemeFontSizeOverride("font_size", 13);
        hRow.AddChild(_hoverBannerLabel);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.BronzeAccent, 6);
        panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        panel.OffsetLeft = -335;
        panel.OffsetRight = 335;
        panel.OffsetTop = -56;
        panel.OffsetBottom = -10;
        _hudRoot.AddChild(panel);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 6);
        panel.AddChild(row);

        foreach (var mode in Enum.GetValues<MapLensMode>())
        {
            var m = mode;
            var btn = UiThemeFactory.CreateStyledButton(string.Empty, 122, 30, UiThemeFactory.SteelBorder);
            btn.Pressed += () =>
            {
                _currentLens = m;
                UpdateTabAndLensHighlights();
                LensModeChanged?.Invoke(m);
            };
            _lensButtons[m] = btn;
            row.AddChild(btn);
        }
    }

    private void BuildPauseMenu()
    {
        _pauseOverlay = new ColorRect { Visible = false, Color = new Color(0.02f, 0.02f, 0.03f, 0.72f) };
        _pauseOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_pauseOverlay);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _pauseOverlay.AddChild(center);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 20);
        panel.CustomMinimumSize = new Vector2(360, 310);
        center.AddChild(panel);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        panel.AddChild(v);

        _pauseTitle = UiThemeFactory.CreateHeader(string.Empty, 22, UiThemeFactory.GoldAccent);
        _pauseTitle.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_pauseTitle);
        v.AddChild(new HSeparator());

        _resumeBtn = UiThemeFactory.CreateStyledButton(string.Empty, 0, 40, UiThemeFactory.GoldAccent);
        _resumeBtn.Pressed += () =>
        {
            _pauseOverlay.Visible = false;
            CommandIssued?.Invoke(new SetPausedCommand(false));
        };
        v.AddChild(_resumeBtn);

        _quicksaveBtn = UiThemeFactory.CreateStyledButton(string.Empty, 0, 40, UiThemeFactory.SteelBorder);
        _quicksaveBtn.Pressed += () => QuicksaveRequested?.Invoke();
        v.AddChild(_quicksaveBtn);

        _pauseSettingsBtn = UiThemeFactory.CreateStyledButton(string.Empty, 0, 40, UiThemeFactory.SteelBorder);
        _pauseSettingsBtn.Pressed += () => _settingsModal.Open(_settings);
        v.AddChild(_pauseSettingsBtn);

        _toMenuBtn = UiThemeFactory.CreateStyledButton(string.Empty, 0, 40, UiThemeFactory.CrimsonAlert);
        _toMenuBtn.Pressed += () =>
        {
            _pauseOverlay.Visible = false;
            ReturnToMainMenuRequested?.Invoke();
        };
        v.AddChild(_toMenuBtn);

        _pauseStatusLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _pauseStatusLabel.AddThemeColorOverride("font_color", UiThemeFactory.EmeraldPositive);
        v.AddChild(_pauseStatusLabel);
    }

    private void ToggleLeftTab(ActiveDrawerTab tab)
    {
        if (_activeTab == tab && _leftDrawer.Visible)
        {
            _activeTab = ActiveDrawerTab.None;
            _leftDrawer.Visible = false;
            UpdateTabAndLensHighlights();
            if (_latestSnapshot is not null)
                RefreshAlertStrip(_latestSnapshot);
            return;
        }
        _activeTab = tab;
        _leftDrawer.Visible = true;
        UpdateTabAndLensHighlights();
        if (_latestSnapshot is not null)
            RefreshAlertStrip(_latestSnapshot);
        RenderActiveDrawerTab();
    }

    private void UpdateTabAndLensHighlights()
    {
        foreach (var (tab, btn) in _tabButtons)
            UiThemeFactory.ApplyButtonHighlight(btn, _leftDrawer.Visible && _activeTab == tab, UiThemeFactory.SteelBorder);

        foreach (var (mode, btn) in _lensButtons)
            UiThemeFactory.ApplyButtonHighlight(btn, mode == _currentLens, UiThemeFactory.SteelBorder);
    }

    private void RefreshTopBarAndDynasty(RenderSnapshot s)
    {
        var l = _settings.ResolveActiveLanguage();
        var pt = l.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var goldLabel = _loc.Get(l, "currency.gold");

        var playerState = s.States.FirstOrDefault(st => st.OwnerNationId == s.PlayerNationId);
        var nationColor = UiThemeFactory.ParseHexColor(playerState?.OwnerColorHex, UiThemeFactory.GoldAccent);
        _nationCrestColor.Color = nationColor;
        _nationTagLabel.Text = s.PlayerNationId;
        _nationNameLabel.Text = s.PlayerNationName;

        var net = s.WeeklyBalance.ToGold();
        var sign = net >= 0 ? "+" : string.Empty;
        _treasuryPillBtn.Text = $"{s.Treasury.ToGold():N0} {goldLabel} ({sign}{net:N0})";
        UiThemeFactory.ApplyButtonHighlight(_treasuryPillBtn, false, net < 0 ? UiThemeFactory.CrimsonAlert : UiThemeFactory.BronzeAccent);
        _treasuryPillBtn.AddThemeColorOverride("font_color", net < 0 ? UiThemeFactory.CrimsonAlert : UiThemeFactory.GoldAccent);

        _bureaucracyPillLabel.Text = $"Bur {(s.BureaucracyBalance >= 0 ? "+" : "")}{s.BureaucracyBalance}";
        _bureaucracyPillLabel.AddThemeColorOverride("font_color", s.BureaucracyBalance < 0 ? UiThemeFactory.CrimsonAlert : UiThemeFactory.CyanAether);

        _authorityPillLabel.Text = $"Aut {s.AuthorityLevel}";

        var stressCol = s.RulerStress >= 200 ? UiThemeFactory.CrimsonAlert
                      : s.RulerStress >= 100 ? UiThemeFactory.AmberAlert
                      : UiThemeFactory.EmeraldPositive;
        _stressPillBtn.Text = $"Est {s.RulerStress}/300";
        UiThemeFactory.ApplyButtonHighlight(_stressPillBtn, false, stressCol);
        _stressPillBtn.AddThemeColorOverride("font_color", stressCol);

        _populationPillLabel.Text = s.PlayerPopulation >= 1_000_000
            ? $"Pop {s.PlayerPopulation / 1_000_000.0:F2}M"
            : $"Pop {s.PlayerPopulation:N0}";

        _dateLabel.Text = s.Date.ToDisplayString();
        _pauseBtn.Text = s.IsPaused ? _loc.Get(l, "hud.play") : _loc.Get(l, "hud.pause");
        UiThemeFactory.ApplyButtonHighlight(_pauseBtn, s.IsPaused, UiThemeFactory.BronzeAccent);
        for (var i = 0; i < 5; i++)
            UiThemeFactory.ApplyButtonHighlight(_speedButtons[i], s.Speed == i + 1, UiThemeFactory.SteelBorder);

        // Dynasty Card
        _portraitCrestFill.Color = nationColor.Darkened(0.35f);
        _portraitMonogramLabel.Text = s.PlayerNationId.Length > 0 ? s.PlayerNationId[..Math.Min(2, s.PlayerNationId.Length)] : "★";
        _dynastyRulerLabel.Text = $"{_loc.Get(l, "hud.ruler")}: {s.RulerName}";
        _dynastyHeirLabel.Text = $"{_loc.Get(l, "hud.heir")}: {s.HeirName}";

        if (_lastRenderedAncestry != s.RulerAncestryDisplay)
        {
            _lastRenderedAncestry = s.RulerAncestryDisplay;
            UiThemeFactory.ClearChildren(_ancestryPillsFlow);
            var parts = s.RulerAncestryDisplay.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                parts = [s.RulerAncestryDisplay];

            foreach (var part in parts)
            {
                var pill = UiThemeFactory.CreatePill(UiThemeFactory.CardBg, UiThemeFactory.CyanAether, 7, 1);
                var lbl = new Label { Text = part };
                lbl.AddThemeFontSizeOverride("font_size", 11);
                lbl.AddThemeColorOverride("font_color", UiThemeFactory.CyanAether);
                pill.AddChild(lbl);
                _ancestryPillsFlow.AddChild(pill);
            }
        }

        _dynastyStressBar.Value = Math.Clamp(s.RulerStress, 0, 300);
        if (_dynastyStressBar.GetThemeStylebox("fill") is StyleBoxFlat fillBox)
            fillBox.BgColor = stressCol;
        _dynastyStressValLabel.Text = pt ? $"Estresse {s.RulerStress}/300" : $"Stress {s.RulerStress}/300";
        _dynastyStressValLabel.AddThemeColorOverride("font_color", stressCol);
    }

    private void RefreshAlertStrip(RenderSnapshot s)
    {
        UiThemeFactory.ClearChildren(_alertRow);
        if (_leftDrawer.Visible)
        {
            _alertStrip.Visible = false;
            return;
        }

        var pt = _settings.ResolveActiveLanguage().StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var added = 0;

        if (s.PlayerDiplomacy.Any(d => d.AtWar))
        {
            AddSituationAlert(pt ? "Em Guerra!" : "At War!", UiThemeFactory.CrimsonAlert, ActiveDrawerTab.Diplomacy);
            added++;
        }
        if (!s.PlayerLaws.Any(l => l.IsEnacting))
        {
            AddSituationAlert(pt ? "Sem Lei" : "No Law", UiThemeFactory.GoldAccent, ActiveDrawerTab.Politics);
            added++;
        }
        if (!s.PlayerTechnologies.Any(t => t.IsActive))
        {
            AddSituationAlert(pt ? "Sem Pesquisa" : "No Tech", UiThemeFactory.CyanAether, ActiveDrawerTab.Technology);
            added++;
        }
        if (s.WeeklyBalance.CopperUnits < 0)
        {
            AddSituationAlert(pt ? "Déficit" : "Deficit", UiThemeFactory.AmberAlert, ActiveDrawerTab.Council);
            added++;
        }
        if (s.PlayerJournalEntries.Any(j => !j.Completed))
        {
            AddSituationAlert(pt ? "Diários" : "Journal", UiThemeFactory.EmeraldPositive, ActiveDrawerTab.Journal);
            added++;
        }

        _alertStrip.Visible = added > 0;
    }

    private void AddSituationAlert(string label, Color accent, ActiveDrawerTab targetTab)
    {
        var btn = UiThemeFactory.CreateStyledButton(label, 0, 24, accent);
        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.AddThemeColorOverride("font_color", accent);
        btn.Pressed += () => ToggleLeftTab(targetTab);
        _alertRow.AddChild(btn);
    }

    private void RefreshOutliner(RenderSnapshot s)
    {
        UiThemeFactory.ClearChildren(_outlinerBody);
        var l = _settings.ResolveActiveLanguage();

        var activeLaw = s.PlayerLaws.FirstOrDefault(x => x.IsEnacting);
        if (activeLaw is not null)
        {
            AddOutlinerProgressCard(
                $"{_loc.Get(l, "tab.politics")}: {_loc.Get(l, activeLaw.NameKey)} ({activeLaw.EnactmentProgress * 100:F0}%)",
                (float)(activeLaw.EnactmentProgress * 100.0), 100f, UiThemeFactory.GoldAccent);
        }

        var activeTech = s.PlayerTechnologies.FirstOrDefault(x => x.IsActive);
        if (activeTech is not null)
        {
            AddOutlinerProgressCard(
                $"{_loc.Get(l, "tab.tech")}: {_loc.Get(l, activeTech.NameKey)} ({activeTech.CurrentProgress:F0}/{activeTech.CostPoints:F0})",
                (float)activeTech.CurrentProgress, (float)activeTech.CostPoints, UiThemeFactory.CyanAether);
        }

        foreach (var je in s.PlayerJournalEntries.Where(j => !j.Completed).Take(2))
        {
            AddOutlinerProgressCard(
                $"{_loc.Get(l, "tab.journal")}: {_loc.Get(l, je.NameKey)} ({je.ProgressRatio * 100:F0}%)",
                (float)(je.ProgressRatio * 100.0), 100f, UiThemeFactory.EmeraldPositive);
        }

        var occupiedCount = s.Provinces.Count(p => p.OwnerNationId == s.PlayerNationId && p.OccupiedByNationId is not null);
        if (occupiedCount > 0)
            AddOutlinerLine($"[!] {_loc.Get(l, "province.occupied_by")}: {occupiedCount} prov.", UiThemeFactory.CrimsonAlert);

        _outlinerBody.AddChild(new HSeparator());
        foreach (var dip in s.PlayerDiplomacy.Where(d => d.AtWar || d.Alliance || d.TradePact).Take(4))
        {
            var tag = dip.AtWar ? "GUERRA" : dip.Alliance ? "Aliado" : "Comércio";
            var col = dip.AtWar ? UiThemeFactory.CrimsonAlert : (dip.Alliance ? UiThemeFactory.EmeraldPositive : UiThemeFactory.CyanAether);
            AddOutlinerLine($"[{dip.TargetNationId}] {tag} ({dip.Opinion:+0;-0;0})", col);
        }
    }

    private void AddOutlinerProgressCard(string text, float val, float max, Color color)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 3);
        var lbl = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", color);
        box.AddChild(lbl);
        box.AddChild(UiThemeFactory.CreateStyledProgressBar(color, max, val, 5f));
        _outlinerBody.AddChild(box);
    }

    private void AddOutlinerLine(string text, Color color)
    {
        var lbl = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", color);
        _outlinerBody.AddChild(lbl);
    }

    private void RenderActiveDrawerTab()
    {
        if (_latestSnapshot is null) return;
        UiThemeFactory.ClearChildren(_leftDrawerBody);
        var s = _latestSnapshot;
        var l = _settings.ResolveActiveLanguage();
        var goldLabel = _loc.Get(l, "currency.gold");

        _leftDrawerTitle.Text = _tabButtons.TryGetValue(_activeTab, out var btn) ? btn.Text : _activeTab.ToString();

        switch (_activeTab)
        {
            case ActiveDrawerTab.Council:
                _leftDrawerBody.AddChild(new Label
                {
                    Text = $"{_loc.Get(l, "hud.tax_rate")}: {s.TaxRate * 100:F0}%  |  {_loc.Get(l, "hud.stress")}: {s.RulerStress}/300",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                });
                var taxRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                taxRow.AddThemeConstantOverride("h_separation", 6);
                taxRow.AddThemeConstantOverride("v_separation", 6);
                foreach (var rate in new[] { 0.08, 0.12, 0.18, 0.25 })
                {
                    var r = rate;
                    var isCurTax = Math.Abs(s.TaxRate - r) < 0.005;
                    var b = UiThemeFactory.CreateStyledButton($"{r * 100:F0}%", 60, 30, UiThemeFactory.SteelBorder, isCurTax);
                    b.Pressed += () => CommandIssued?.Invoke(new SetTaxRateCommand(s.PlayerNationId, r));
                    taxRow.AddChild(b);
                }
                _leftDrawerBody.AddChild(taxRow);
                _leftDrawerBody.AddChild(new HSeparator());

                AddActionButton($"{_loc.Get(l, "court.banquet")} (300 {goldLabel}, -20 Estresse)", () => CommandIssued?.Invoke(new CourtActionCommand(s.PlayerNationId, CourtActionKind.RoyalBanquet)));
                AddActionButton($"{_loc.Get(l, "court.research_grant")} (400 {goldLabel}, +120 Tec)", () => CommandIssued?.Invoke(new CourtActionCommand(s.PlayerNationId, CourtActionKind.ResearchGrant)));
                AddActionButton(_loc.Get(l, "court.raise_authority"), () => CommandIssued?.Invoke(new CourtActionCommand(s.PlayerNationId, CourtActionKind.RaiseAuthority)));
                AddActionButton(_loc.Get(l, "court.lower_authority"), () => CommandIssued?.Invoke(new CourtActionCommand(s.PlayerNationId, CourtActionKind.LowerAuthority)));
                break;

            case ActiveDrawerTab.Market:
                foreach (var (goodId, price) in s.MarketPricesGold.OrderBy(k => k.Key))
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 8);
                    var row = new HBoxContainer();
                    row.AddChild(new Label { Text = _loc.Get(l, $"good.{goodId}"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
                    var pLbl = new Label { Text = $"{price:F1} {goldLabel}" };
                    pLbl.AddThemeColorOverride("font_color", UiThemeFactory.GoldAccent);
                    row.AddChild(pLbl);
                    card.AddChild(row);
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Pops:
                foreach (var p in s.PlayerPops.OrderByDescending(x => x.Population).Take(30))
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 8);
                    card.AddChild(new Label
                    {
                        Text = $"{p.StateName} | {p.Profession} ({p.AncestryDisplay})\nPop: {p.Population:N0}  |  SoL: {p.StandardOfLiving:F1}  |  Lit: {p.Literacy * 100:F0}%",
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Politics:
                foreach (var ig in s.PlayerInterestGroups.OrderByDescending(x => x.CloutPercent))
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, ig.InGovernment ? UiThemeFactory.EmeraldPositive : UiThemeFactory.SteelBorder, 7);
                    var box = new VBoxContainer();
                    box.AddThemeConstantOverride("separation", 4);
                    var top = new HBoxContainer();
                    top.AddChild(new Label
                    {
                        Text = $"{ig.DisplayName}" + (ig.InGovernment ? " [GOV]" : string.Empty),
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                    });
                    var meta = new Label { Text = $"{ig.CloutPercent:F1}% | {ig.Approval:+0;-0;0}" };
                    meta.AddThemeColorOverride("font_color", ig.Approval >= 0 ? UiThemeFactory.EmeraldPositive : UiThemeFactory.CrimsonAlert);
                    top.AddChild(meta);
                    box.AddChild(top);
                    box.AddChild(UiThemeFactory.CreateStyledProgressBar(ig.InGovernment ? UiThemeFactory.GoldAccent : UiThemeFactory.BronzeAccent, 100f, (float)ig.CloutPercent, 6f));
                    card.AddChild(box);
                    _leftDrawerBody.AddChild(card);
                }
                _leftDrawerBody.AddChild(new HSeparator());
                foreach (var law in s.PlayerLaws)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, law.IsEnacted ? UiThemeFactory.EmeraldPositive : (law.IsEnacting ? UiThemeFactory.GoldAccent : UiThemeFactory.SteelBorder), 8);
                    var vBox = new VBoxContainer();
                    vBox.AddThemeConstantOverride("separation", 4);
                    var row = new HBoxContainer();
                    row.AddChild(new Label
                    {
                        Text = $"[{law.Category}] {_loc.Get(l, law.NameKey)}",
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                    var status = law.IsEnacted ? _loc.Get(l, "law.enacted")
                        : law.IsEnacting ? $"{law.EnactmentProgress * 100:F0}%"
                        : _loc.Get(l, "law.enact");
                    var btnLaw = UiThemeFactory.CreateStyledButton(status, 86, 28, UiThemeFactory.SteelBorder, law.IsEnacted);
                    btnLaw.Disabled = law.IsEnacted || law.IsEnacting;
                    var lid = law.Id;
                    btnLaw.Pressed += () => CommandIssued?.Invoke(new EnactLawCommand(s.PlayerNationId, lid));
                    row.AddChild(btnLaw);
                    vBox.AddChild(row);
                    if (law.IsEnacting)
                        vBox.AddChild(UiThemeFactory.CreateStyledProgressBar(UiThemeFactory.GoldAccent, 100f, (float)(law.EnactmentProgress * 100.0), 5f));
                    card.AddChild(vBox);
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Technology:
                foreach (var tech in s.PlayerTechnologies)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, tech.Completed ? UiThemeFactory.EmeraldPositive : (tech.IsActive ? UiThemeFactory.CyanAether : UiThemeFactory.SteelBorder), 8);
                    var vBox = new VBoxContainer();
                    vBox.AddThemeConstantOverride("separation", 4);
                    var row = new HBoxContainer();
                    row.AddChild(new Label
                    {
                        Text = $"[{tech.Tree}] {_loc.Get(l, tech.NameKey)} ({tech.CurrentProgress:F0}/{tech.CostPoints:F0})",
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                    var btnTech = UiThemeFactory.CreateStyledButton(
                        tech.Completed ? _loc.Get(l, "tech.completed") : (tech.IsActive ? _loc.Get(l, "tech.active") : _loc.Get(l, "tech.research")),
                        96, 28, UiThemeFactory.SteelBorder, tech.IsActive || tech.Completed);
                    btnTech.Disabled = tech.Completed || tech.IsActive;
                    var tid = tech.Id;
                    btnTech.Pressed += () => CommandIssued?.Invoke(new StartResearchCommand(s.PlayerNationId, tid));
                    row.AddChild(btnTech);
                    vBox.AddChild(row);
                    vBox.AddChild(UiThemeFactory.CreateStyledProgressBar(tech.Completed ? UiThemeFactory.EmeraldPositive : UiThemeFactory.CyanAether, (float)tech.CostPoints, (float)tech.CurrentProgress, 5f));
                    card.AddChild(vBox);
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Diplomacy:
                foreach (var dip in s.PlayerDiplomacy)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, dip.AtWar ? UiThemeFactory.CrimsonAlert : UiThemeFactory.SteelBorder, 8);
                    var box = new VBoxContainer();
                    box.AddThemeConstantOverride("separation", 5);
                    box.AddChild(new Label
                    {
                        Text = $"[{dip.TargetNationId}] {dip.TargetNationName} | Opinião: {dip.Opinion:+0;-0;0} | Poder: {dip.EstimatedPower:F0}" +
                               (dip.AtWar ? $" | GUERRA ({dip.WarScore:F0}%)" : string.Empty),
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                    if (dip.AtWar)
                        box.AddChild(UiThemeFactory.CreateStyledProgressBar(UiThemeFactory.CrimsonAlert, 100f, Math.Clamp((float)(dip.WarScore + 50.0), 0f, 100f), 6f));
                    var acts = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    acts.AddThemeConstantOverride("h_separation", 6);
                    acts.AddThemeConstantOverride("v_separation", 4);
                    var target = dip.TargetNationId;
                    AddMiniButton(acts, _loc.Get(l, "diplo.improve"), () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, DiplomaticActionKind.ImproveRelations)));
                    AddMiniButton(acts, _loc.Get(l, "diplo.trade"), () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, DiplomaticActionKind.ToggleTradePact)));
                    AddMiniButton(acts, _loc.Get(l, "diplo.alliance"), () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, DiplomaticActionKind.ToggleAlliance)));
                    AddMiniButton(acts, dip.AtWar ? _loc.Get(l, "diplo.peace") : _loc.Get(l, "diplo.war"),
                        () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, dip.AtWar ? DiplomaticActionKind.MakePeace : DiplomaticActionKind.DeclareWar)),
                        dip.AtWar ? UiThemeFactory.EmeraldPositive : UiThemeFactory.CrimsonAlert);
                    box.AddChild(acts);
                    card.AddChild(box);
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Journal:
                foreach (var je in s.PlayerJournalEntries)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, je.Completed ? UiThemeFactory.EmeraldPositive : UiThemeFactory.SteelBorder, 10);
                    var vBox = new VBoxContainer();
                    vBox.AddThemeConstantOverride("separation", 5);
                    vBox.AddChild(new Label
                    {
                        Text = $"{_loc.Get(l, je.NameKey)} ({(je.Completed ? "OK" : $"{je.ProgressRatio * 100:F0}%")})\n{_loc.Get(l, je.DescriptionKey)}\n+{je.RewardGold:F0} {goldLabel}",
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                    vBox.AddChild(UiThemeFactory.CreateStyledProgressBar(je.Completed ? UiThemeFactory.EmeraldPositive : UiThemeFactory.GoldAccent, 100f, (float)(je.ProgressRatio * 100.0), 6f));
                    card.AddChild(vBox);
                    _leftDrawerBody.AddChild(card);
                }
                break;
        }
    }

    private void RenderStateInspector(string stateId)
    {
        if (_latestSnapshot is null) return;
        var st = _latestSnapshot.States.FirstOrDefault(x => x.StateId == stateId);
        if (st is null) return;

        var l = _settings.ResolveActiveLanguage();
        var goldLabel = _loc.Get(l, "currency.gold");
        var isPlayerState = st.OwnerNationId == _latestSnapshot.PlayerNationId;

        _stateTitleLabel.Text = st.IsPlayerCapital ? $"★ {st.Name}" : st.Name;
        var resText = st.ResourceTags.Count > 0 ? string.Join(", ", st.ResourceTags) : "—";
        var geoBreadcrumb = !string.IsNullOrWhiteSpace(st.LocalAdminTitle)
            ? $"{st.LocalAdminTitle}  •  {st.RegionId ?? "Pontária"}\n"
            : string.Empty;

        _stateMetaLabel.Text =
            geoBreadcrumb +
            $"{st.OwnerNationId}  |  {_loc.Get(l, "state.population")}: {st.Population:N0}  |  {_loc.Get(l, "state.sol")}: {st.AverageSoL:F1}\n" +
            $"{_loc.Get(l, "state.arable_land")}: {st.ArableLand}  |  {_loc.Get(l, "state.resources")}: {resText}";

        UiThemeFactory.ClearChildren(_provinceSelectorFlow);
        var stateProvinces = _latestSnapshot.Provinces.Where(p => p.StateId == stateId).ToList();
        var activeProv = stateProvinces.FirstOrDefault(p => p.ProvinceId == _selectedProvinceId)
                      ?? stateProvinces.FirstOrDefault(p => p.IsCapital)
                      ?? stateProvinces.FirstOrDefault();

        if (activeProv is not null)
            _selectedProvinceId = activeProv.ProvinceId;

        foreach (var prov in stateProvinces)
        {
            var isSel = activeProv is not null && prov.ProvinceId == activeProv.ProvinceId;
            var badge = prov.IsCapital ? "★ " : string.Empty;
            var occBadge = prov.OccupiedByNationId is not null ? " [!]" : string.Empty;
            var pBtn = UiThemeFactory.CreateStyledButton($"{badge}{prov.Name} (T{prov.HoldingLevel}){occBadge}", 0, 28, UiThemeFactory.SteelBorder, isSel);
            var capturedProv = prov;
            pBtn.Pressed += () => SelectProvince(capturedProv.ProvinceId, capturedProv.StateId);
            _provinceSelectorFlow.AddChild(pBtn);
        }

        UiThemeFactory.ClearChildren(_activeProvinceBody);
        if (activeProv is not null)
        {
            // EU4-style Biome Terrain Color Header Banner
            var terrainBand = UiThemeFactory.CreatePill(UiThemeFactory.GetTerrainBannerColor(activeProv.Terrain), UiThemeFactory.BronzeAccent, 8, 5);
            _activeProvinceBody.AddChild(terrainBand);

            var terrainLabel = _loc.Get(l, $"terrain.{activeProv.Terrain}");
            var hubLabel = _loc.Get(l, $"hub.{activeProv.HubType}");
            var dynTitle = !string.IsNullOrWhiteSpace(activeProv.DynamicDisplayName) && activeProv.DynamicDisplayName != activeProv.Name
                ? $"{activeProv.DynamicDisplayName} ({activeProv.Name})"
                : activeProv.Name;

            var bandRow = new HBoxContainer();
            bandRow.AddChild(UiThemeFactory.CreateHeader(
                $"{(activeProv.IsCapital ? "★ " : string.Empty)}{dynTitle}" + (activeProv.IsCoastal ? "  ⚓" : string.Empty),
                14,
                Colors.White));
            bandRow.GetChild<Label>(0).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var terrainTag = new Label { Text = terrainLabel.ToUpperInvariant() };
            terrainTag.AddThemeFontSizeOverride("font_size", 11);
            terrainTag.AddThemeColorOverride("font_color", UiThemeFactory.GoldAccent);
            bandRow.AddChild(terrainTag);
            terrainBand.AddChild(bandRow);

            var detailText =
                $"{hubLabel}" +
                (activeProv.ResourcePotential is not null ? $"  •  {activeProv.ResourcePotential}" : string.Empty) +
                $"   |   {_loc.Get(l, "province.holding")}: {activeProv.HoldingLevel}/10   •   {_loc.Get(l, "province.fort")}: {activeProv.FortLevel}/5";
            if (activeProv.OccupiedByNationId is not null)
                detailText += $"\n{_loc.Get(l, "province.occupied_by")}: {activeProv.OccupiedByNationId}";

            var infoLbl = new Label { Text = detailText, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            infoLbl.AddThemeFontSizeOverride("font_size", 12);
            if (activeProv.OccupiedByNationId is not null)
                infoLbl.AddThemeColorOverride("font_color", UiThemeFactory.CrimsonAlert);
            _activeProvinceBody.AddChild(infoLbl);

            // Visual Holding & Fort Level Progress Gauges
            var gaugeRow = new HBoxContainer();
            gaugeRow.AddThemeConstantOverride("separation", 8);
            gaugeRow.AddChild(UiThemeFactory.CreateStyledProgressBar(UiThemeFactory.GoldAccent, 10f, activeProv.HoldingLevel, 6f));
            gaugeRow.AddChild(UiThemeFactory.CreateStyledProgressBar(UiThemeFactory.CyanAether, 5f, activeProv.FortLevel, 6f));
            _activeProvinceBody.AddChild(gaugeRow);

            if (isPlayerState)
            {
                var provActions = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                provActions.AddThemeConstantOverride("h_separation", 8);
                provActions.AddThemeConstantOverride("v_separation", 6);
                var holdingCost = 220 + activeProv.HoldingLevel * 90;
                var fortCost = 180 + activeProv.FortLevel * 110;
                var pId = activeProv.ProvinceId;

                var holdBtn = UiThemeFactory.CreateStyledButton($"{_loc.Get(l, "province.expand_holding")} ({holdingCost} {goldLabel})", 0, 28, UiThemeFactory.BronzeAccent);
                holdBtn.Disabled = activeProv.HoldingLevel >= 10;
                holdBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeProvinceHoldingCommand(pId, "holding"));
                provActions.AddChild(holdBtn);

                var fortBtn = UiThemeFactory.CreateStyledButton($"{_loc.Get(l, "province.fortify")} ({fortCost} {goldLabel})", 0, 28, UiThemeFactory.CyanAether);
                fortBtn.Disabled = activeProv.FortLevel >= 5;
                fortBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeProvinceHoldingCommand(pId, "fort"));
                provActions.AddChild(fortBtn);

                _activeProvinceBody.AddChild(provActions);
            }
        }

        UiThemeFactory.ClearChildren(_stateBuildingsBox);
        var bSectionHdr = UiThemeFactory.CreateHeader(_loc.Get(l, "state.buildings_title"), 14, UiThemeFactory.CyanAether);
        _stateBuildingsBox.AddChild(bSectionHdr);

        foreach (var b in st.Buildings)
        {
            var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 8);
            var bv = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            bv.AddThemeConstantOverride("separation", 5);
            card.AddChild(bv);

            var topRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            topRow.AddThemeConstantOverride("separation", 6);
            topRow.AddChild(new Label
            {
                Text = $"{_loc.Get(l, b.NameKey)} — Lv.{b.Levels}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            });
            var expandBtn = UiThemeFactory.CreateStyledButton("+1", 44, 28, UiThemeFactory.GoldAccent);
            expandBtn.Disabled = !isPlayerState;
            var bid = b.BuildingId;
            expandBtn.Pressed += () => CommandIssued?.Invoke(new BuildStructureCommand(st.StateId, bid, 1));
            topRow.AddChild(expandBtn);
            bv.AddChild(topRow);

            var pmFlow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pmFlow.AddThemeConstantOverride("h_separation", 6);
            pmFlow.AddThemeConstantOverride("v_separation", 4);
            foreach (var pm in b.AvailablePms)
            {
                var isCurPm = pm.PmId == b.ActivePmId;
                var pmBtn = UiThemeFactory.CreateStyledButton(_loc.Get(l, pm.NameKey), 0, 26, UiThemeFactory.SteelBorder, isCurPm);
                pmBtn.Disabled = !isPlayerState || !pm.Unlocked || isCurPm;
                var pmid = pm.PmId;
                pmBtn.Pressed += () => CommandIssued?.Invoke(new SwitchProductionMethodCommand(st.StateId, bid, pmid));
                pmFlow.AddChild(pmBtn);
            }
            bv.AddChild(pmFlow);
            _stateBuildingsBox.AddChild(card);
        }
    }

    private void AddActionButton(string text, Action onPressed)
    {
        var b = UiThemeFactory.CreateStyledButton(text, 0, 34, UiThemeFactory.BronzeAccent);
        b.Alignment = HorizontalAlignment.Left;
        b.Pressed += onPressed;
        _leftDrawerBody.AddChild(b);
    }

    private static void AddMiniButton(Control parent, string text, Action onPressed, Color? border = null)
    {
        var b = UiThemeFactory.CreateStyledButton(text, 0, 26, border ?? UiThemeFactory.SteelBorder);
        b.Pressed += onPressed;
        parent.AddChild(b);
    }
}
