using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

/// <summary>
/// Full Paradox-grade Grand Strategy HUD built 100% code-first over the MapView2D:
/// Top bar (always fits cleanly), Left Navigation Rail + Drawers, Right Outliner OR State+Province Inspector,
/// Bottom-Left Dynasty card, Hover Bar, Bottom-Center Map Lenses, Nested Tooltips, and Pause Menu.
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

    private Control _hudRoot = null!;

    // Top Bar
    private Label _nationDateLabel = null!;
    private Label _topTelemetryLabel = null!;
    private Button _pauseBtn = null!;
    private readonly Button[] _speedButtons = new Button[5];
    private Button _menuBtn = null!;

    // Left Navigation Rail & Drawer
    private VBoxContainer _leftNavBox = null!;
    private readonly Dictionary<ActiveDrawerTab, Button> _tabButtons = new();
    private PanelContainer _leftDrawer = null!;
    private Label _leftDrawerTitle = null!;
    private Button _leftDrawerCloseBtn = null!;
    private VBoxContainer _leftDrawerBody = null!;

    // Right Outliner & State+Province Inspector (mutually exclusive to eliminate overlap)
    private PanelContainer _outlinerPanel = null!;
    private Label _outlinerTitle = null!;
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

    // Bottom-Left Dynasty & Court Box
    private PanelContainer _dynastyPanel = null!;
    private Label _dynastyLabel = null!;
    private Button _dynastyTooltipBtn = null!;

    // Bottom-Center Map Lenses & Hover Strip
    private PanelContainer _hoverBanner = null!;
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

    public event Action<GameCommand>? CommandIssued;
    public event Action<MapLensMode>? LensModeChanged;
    public event Action<string>? FocusStateRequested;
    public event Action? QuicksaveRequested;
    public event Action? ReturnToMainMenuRequested;
    public event Action<GameSettings>? SettingsChanged;

    public bool IsPauseMenuOpen => _pauseOverlay.Visible;
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
        BuildLeftNavAndDrawer();
        BuildOutliner();
        BuildStateInspector();
        BuildDynastyCard();
        BuildHoverBannerAndBottomLenses();
        BuildPauseMenu();

        _settingsModal = new SettingsModal(_loc, _settings) { Visible = false };
        _settingsModal.SettingsApplied += s =>
        {
            _settings = s;
            SettingsChanged?.Invoke(s);
            ApplyLocalization();
            RefreshCurrentViews();
        };
        AddChild(_settingsModal);

        _tooltip = new NestedTooltipManager();
        AddChild(_tooltip);

        ApplyLocalization();
    }

    public void SetSettings(GameSettings settings)
    {
        _settings = settings;
        ApplyLocalization();
        RefreshCurrentViews();
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

        _hoverBannerLabel.Text = $"{prov.Name} [{ownerTag}]  |  {state?.Name ?? prov.StateId}  |  {terrainName}  •  {hubName} Lv.{prov.HoldingLevel}"
            + (prov.FortLevel > 0 ? $"  •  Fort {prov.FortLevel}" : string.Empty);
        _hoverBanner.Visible = true;
    }

    internal void QaOpenTab(string tabName)
    {
        if (Enum.TryParse<ActiveDrawerTab>(tabName, out var parsed))
        {
            _activeTab = parsed;
            _leftDrawer.Visible = parsed != ActiveDrawerTab.None;
            if (_leftDrawer.Visible)
                RenderActiveDrawerTab();
        }
    }

    public string InspectLayoutBounds()
    {
        var vpSize = _hudRoot.GetViewportRect().Size;
        var issues = new List<string>();

        CheckControlBounds("TopMenuBtn", _menuBtn, vpSize, issues);
        CheckControlBounds("DynastyPanel", _dynastyPanel, vpSize, issues);
        if (_leftDrawer.Visible)
            CheckControlBounds("LeftDrawer", _leftDrawer, vpSize, issues);
        if (_leftDrawer.Visible && _dynastyPanel.GetGlobalRect().Intersects(_leftDrawer.GetGlobalRect()))
            issues.Add("OVERLAP(Dynasty,LeftDrawer)");
        if (_outlinerPanel.Visible)
            CheckControlBounds("Outliner", _outlinerPanel, vpSize, issues);
        if (_stateDrawer.Visible)
            CheckControlBounds("StateInspector", _stateDrawer, vpSize, issues);

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

        _lensButtons[MapLensMode.Political].Text = _loc.Get(l, "lens.political");
        _lensButtons[MapLensMode.Economic].Text = _loc.Get(l, "lens.economic");
        _lensButtons[MapLensMode.Ancestry].Text = _loc.Get(l, "lens.ancestry");
        _lensButtons[MapLensMode.StandardOfLiving].Text = _loc.Get(l, "lens.sol");
        _lensButtons[MapLensMode.Diplomacy].Text = _loc.Get(l, "lens.diplomacy");

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
        var bar = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 7);
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetBottom = 52;
        _hudRoot.AddChild(bar);

        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);
        bar.AddChild(row);

        _nationDateLabel = UiThemeFactory.CreateHeader("Pontária | 1380 D.Q.", 15, UiThemeFactory.GoldAccent);
        _nationDateLabel.CustomMinimumSize = new Vector2(230, 0);
        row.AddChild(_nationDateLabel);

        _topTelemetryLabel = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        _topTelemetryLabel.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(_topTelemetryLabel);

        _pauseBtn = new Button { CustomMinimumSize = new Vector2(88, 32) };
        _pauseBtn.Pressed += () =>
        {
            if (_latestSnapshot is not null)
                CommandIssued?.Invoke(new SetPausedCommand(!_latestSnapshot.IsPaused));
        };
        row.AddChild(_pauseBtn);

        for (var i = 0; i < 5; i++)
        {
            var spd = i + 1;
            var btn = new Button { Text = $"{spd}x", CustomMinimumSize = new Vector2(32, 32) };
            btn.Pressed += () => CommandIssued?.Invoke(new SetSpeedCommand(spd));
            _speedButtons[i] = btn;
            row.AddChild(btn);
        }

        _menuBtn = new Button { CustomMinimumSize = new Vector2(80, 32) };
        _menuBtn.Pressed += TogglePauseMenu;
        row.AddChild(_menuBtn);
    }

    private void BuildLeftNavAndDrawer()
    {
        _leftNavBox = new VBoxContainer { OffsetLeft = 10, OffsetTop = 62, OffsetRight = 132, OffsetBottom = 430 };
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
            var btn = new Button { CustomMinimumSize = new Vector2(122, 34), Alignment = HorizontalAlignment.Left };
            btn.Pressed += () => ToggleLeftTab(t);
            _tabButtons[t] = btn;
            _leftNavBox.AddChild(btn);
        }

        _leftDrawer = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 14);
        _leftDrawer.Visible = false;
        _leftDrawer.AnchorTop = 0;
        _leftDrawer.AnchorBottom = 1;
        _leftDrawer.OffsetLeft = 140;
        _leftDrawer.OffsetRight = 560;
        _leftDrawer.OffsetTop = 62;
        _leftDrawer.OffsetBottom = -146; // Leaves clean breathing room above Dynasty card!
        _hudRoot.AddChild(_leftDrawer);

        var v = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 8);
        _leftDrawer.AddChild(v);

        var hdr = new HBoxContainer();
        _leftDrawerTitle = UiThemeFactory.CreateHeader(string.Empty, 17, UiThemeFactory.GoldAccent);
        _leftDrawerTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hdr.AddChild(_leftDrawerTitle);
        _leftDrawerCloseBtn = new Button { CustomMinimumSize = new Vector2(76, 30) };
        _leftDrawerCloseBtn.Pressed += () => { _activeTab = ActiveDrawerTab.None; _leftDrawer.Visible = false; };
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
        _outlinerPanel.OffsetLeft = -312;
        _outlinerPanel.OffsetTop = 62;
        _outlinerPanel.OffsetRight = -10;
        _outlinerPanel.OffsetBottom = 360;
        _hudRoot.AddChild(_outlinerPanel);

        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 5);
        _outlinerPanel.AddChild(v);

        _outlinerTitle = UiThemeFactory.CreateHeader("Resumo", 14, UiThemeFactory.GoldAccent);
        v.AddChild(_outlinerTitle);
        v.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _outlinerBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _outlinerBody.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(_outlinerBody);
        v.AddChild(scroll);
    }

    private void BuildStateInspector()
    {
        _stateDrawer = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 14);
        _stateDrawer.Visible = false;
        _stateDrawer.AnchorLeft = 1;
        _stateDrawer.AnchorRight = 1;
        _stateDrawer.AnchorTop = 0;
        _stateDrawer.AnchorBottom = 1;
        _stateDrawer.OffsetLeft = -466;
        _stateDrawer.OffsetRight = -10;
        _stateDrawer.OffsetTop = 62;
        _stateDrawer.OffsetBottom = -78;
        _hudRoot.AddChild(_stateDrawer);

        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 8);
        _stateDrawer.AddChild(v);

        var hdr = new HBoxContainer();
        hdr.AddThemeConstantOverride("separation", 6);
        _stateTitleLabel = UiThemeFactory.CreateHeader(string.Empty, 18, UiThemeFactory.GoldAccent);
        _stateTitleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _stateTitleLabel.ClipText = true;
        hdr.AddChild(_stateTitleLabel);

        _stateFocusBtn = new Button { Text = "⌖", CustomMinimumSize = new Vector2(32, 30) };
        _stateFocusBtn.Pressed += () => { if (_selectedStateId is not null) FocusStateRequested?.Invoke(_selectedStateId); };
        hdr.AddChild(_stateFocusBtn);

        _stateCloseBtn = new Button { CustomMinimumSize = new Vector2(76, 30) };
        _stateCloseBtn.Pressed += CloseStateInspector;
        hdr.AddChild(_stateCloseBtn);
        v.AddChild(hdr);

        _stateMetaLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _stateMetaLabel.AddThemeFontSizeOverride("font_size", 13);
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
        _dynastyPanel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 10);
        _dynastyPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _dynastyPanel.OffsetLeft = 10;
        _dynastyPanel.OffsetTop = -136;
        _dynastyPanel.OffsetRight = 356;
        _dynastyPanel.OffsetBottom = -10;
        _hudRoot.AddChild(_dynastyPanel);

        var v = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 6);
        _dynastyPanel.AddChild(v);

        _dynastyLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _dynastyLabel.AddThemeFontSizeOverride("font_size", 13);
        v.AddChild(_dynastyLabel);

        _dynastyTooltipBtn = new Button { CustomMinimumSize = new Vector2(0, 30) };
        _dynastyTooltipBtn.Pressed += () =>
        {
            _tooltip.ShowConcept("dual_ancestry", 0, _dynastyTooltipBtn.GetGlobalRect().Position);
        };
        v.AddChild(_dynastyTooltipBtn);
    }

    private void BuildHoverBannerAndBottomLenses()
    {
        _hoverBanner = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.CyanAether, 6);
        _hoverBanner.Visible = false;
        _hoverBanner.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _hoverBanner.OffsetLeft = -420;
        _hoverBanner.OffsetRight = 420;
        _hoverBanner.OffsetTop = -98;
        _hoverBanner.OffsetBottom = -62;
        _hoverBanner.MouseFilter = Control.MouseFilterEnum.Ignore;
        _hudRoot.AddChild(_hoverBanner);

        _hoverBannerLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true
        };
        _hoverBannerLabel.AddThemeFontSizeOverride("font_size", 13);
        _hoverBanner.AddChild(_hoverBannerLabel);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 6);
        panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        panel.OffsetLeft = -310;
        panel.OffsetRight = 310;
        panel.OffsetTop = -54;
        panel.OffsetBottom = -10;
        _hudRoot.AddChild(panel);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 6);
        panel.AddChild(row);

        foreach (var mode in Enum.GetValues<MapLensMode>())
        {
            var m = mode;
            var btn = new Button { CustomMinimumSize = new Vector2(110, 30) };
            btn.Pressed += () => LensModeChanged?.Invoke(m);
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

        _resumeBtn = new Button { CustomMinimumSize = new Vector2(0, 40) };
        _resumeBtn.Pressed += () =>
        {
            _pauseOverlay.Visible = false;
            CommandIssued?.Invoke(new SetPausedCommand(false));
        };
        v.AddChild(_resumeBtn);

        _quicksaveBtn = new Button { CustomMinimumSize = new Vector2(0, 40) };
        _quicksaveBtn.Pressed += () => QuicksaveRequested?.Invoke();
        v.AddChild(_quicksaveBtn);

        _pauseSettingsBtn = new Button { CustomMinimumSize = new Vector2(0, 40) };
        _pauseSettingsBtn.Pressed += () => _settingsModal.Open(_settings);
        v.AddChild(_pauseSettingsBtn);

        _toMenuBtn = new Button { CustomMinimumSize = new Vector2(0, 40) };
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
            return;
        }
        _activeTab = tab;
        _leftDrawer.Visible = true;
        RenderActiveDrawerTab();
    }

    private void RefreshTopBarAndDynasty(RenderSnapshot s)
    {
        var l = _settings.ResolveActiveLanguage();
        var goldLabel = _loc.Get(l, "currency.gold");
        var pauseState = s.IsPaused ? $"[{_loc.Get(l, "hud.paused")}]" : $"[{s.Speed}x]";
        _nationDateLabel.Text = $"{s.PlayerNationName}  {pauseState}";

        var sign = s.WeeklyBalance.CopperUnits >= 0 ? "+" : string.Empty;
        _topTelemetryLabel.Text =
            $"{s.Date.ToDisplayString()}  |  {s.Treasury.ToGold():N0} {goldLabel} ({sign}{s.WeeklyBalance.ToGold():N0})  |  " +
            $"{_loc.Get(l, "hud.bureaucracy")}: +{s.BureaucracyBalance}  |  " +
            $"{_loc.Get(l, "hud.authority")}: {s.AuthorityLevel}  |  " +
            $"{_loc.Get(l, "hud.stress")}: {s.RulerStress}  |  Pop: {s.PlayerPopulation:N0}";

        _pauseBtn.Text = s.IsPaused ? _loc.Get(l, "hud.play") : _loc.Get(l, "hud.pause");
        for (var i = 0; i < 5; i++)
            _speedButtons[i].Modulate = s.Speed == i + 1 ? UiThemeFactory.GoldAccent : Colors.White;

        _dynastyLabel.Text =
            $"{_loc.Get(l, "hud.ruler")}: {s.RulerName}\n" +
            $"{_loc.Get(l, "hud.ancestry")}: {s.RulerAncestryDisplay}\n" +
            $"{_loc.Get(l, "hud.heir")}: {s.HeirName}";
    }

    private void RefreshOutliner(RenderSnapshot s)
    {
        UiThemeFactory.ClearChildren(_outlinerBody);
        var l = _settings.ResolveActiveLanguage();

        var activeLaw = s.PlayerLaws.FirstOrDefault(x => x.IsEnacting);
        if (activeLaw is not null)
            AddOutlinerLine($"{_loc.Get(l, "tab.politics")}: {_loc.Get(l, activeLaw.NameKey)} ({activeLaw.EnactmentProgress * 100:F0}%)", UiThemeFactory.GoldAccent);

        var activeTech = s.PlayerTechnologies.FirstOrDefault(x => x.IsActive);
        if (activeTech is not null)
            AddOutlinerLine($"{_loc.Get(l, "tab.tech")}: {_loc.Get(l, activeTech.NameKey)} ({activeTech.CurrentProgress:F0}/{activeTech.CostPoints:F0})", UiThemeFactory.CyanAether);

        foreach (var je in s.PlayerJournalEntries.Where(j => !j.Completed).Take(2))
            AddOutlinerLine($"{_loc.Get(l, "tab.journal")}: {_loc.Get(l, je.NameKey)} ({je.ProgressRatio * 100:F0}%)", Colors.White);

        var occupiedCount = s.Provinces.Count(p => p.OwnerNationId == s.PlayerNationId && p.OccupiedByNationId is not null);
        if (occupiedCount > 0)
            AddOutlinerLine($"{_loc.Get(l, "province.occupied_by")}: {occupiedCount} prov.", UiThemeFactory.CrimsonAlert);

        _outlinerBody.AddChild(new HSeparator());
        foreach (var dip in s.PlayerDiplomacy.Where(d => d.AtWar || d.Alliance || d.TradePact).Take(4))
        {
            var tag = dip.AtWar ? "GUERRA" : dip.Alliance ? "Aliado" : "Comércio";
            var col = dip.AtWar ? UiThemeFactory.CrimsonAlert : UiThemeFactory.EmeraldPositive;
            AddOutlinerLine($"[{dip.TargetNationId}] {tag} ({dip.Opinion:+0;-0;0})", col);
        }
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
                    Text = $"{_loc.Get(l, "hud.tax_rate")}: {s.TaxRate * 100:F0}%  |  {_loc.Get(l, "hud.stress")}: {s.RulerStress}",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                });
                var taxRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                taxRow.AddThemeConstantOverride("h_separation", 6);
                taxRow.AddThemeConstantOverride("v_separation", 6);
                foreach (var rate in new[] { 0.08, 0.12, 0.18, 0.25 })
                {
                    var r = rate;
                    var b = new Button { Text = $"{r * 100:F0}%" };
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
                    row.AddChild(new Label { Text = $"{price:F1} {goldLabel}" });
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
                    _leftDrawerBody.AddChild(new Label
                    {
                        Text = $"{ig.DisplayName}: {ig.CloutPercent:F1}% Clout | Aprovação {ig.Approval:+0;-0;0}" + (ig.InGovernment ? " [GOV]" : string.Empty),
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                }
                _leftDrawerBody.AddChild(new HSeparator());
                foreach (var law in s.PlayerLaws)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, law.IsEnacted ? UiThemeFactory.EmeraldPositive : UiThemeFactory.SteelBorder, 8);
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
                    var btnLaw = new Button { Text = status, Disabled = law.IsEnacted || law.IsEnacting };
                    var lid = law.Id;
                    btnLaw.Pressed += () => CommandIssued?.Invoke(new EnactLawCommand(s.PlayerNationId, lid));
                    row.AddChild(btnLaw);
                    card.AddChild(row);
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Technology:
                foreach (var tech in s.PlayerTechnologies)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, tech.Completed ? UiThemeFactory.EmeraldPositive : (tech.IsActive ? UiThemeFactory.CyanAether : UiThemeFactory.SteelBorder), 8);
                    var row = new HBoxContainer();
                    row.AddChild(new Label
                    {
                        Text = $"[{tech.Tree}] {_loc.Get(l, tech.NameKey)} ({tech.CurrentProgress:F0}/{tech.CostPoints:F0})",
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                    var btnTech = new Button
                    {
                        Text = tech.Completed ? _loc.Get(l, "tech.completed") : (tech.IsActive ? _loc.Get(l, "tech.active") : _loc.Get(l, "tech.research")),
                        Disabled = tech.Completed || tech.IsActive
                    };
                    var tid = tech.Id;
                    btnTech.Pressed += () => CommandIssued?.Invoke(new StartResearchCommand(s.PlayerNationId, tid));
                    row.AddChild(btnTech);
                    card.AddChild(row);
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Diplomacy:
                foreach (var dip in s.PlayerDiplomacy)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, dip.AtWar ? UiThemeFactory.CrimsonAlert : UiThemeFactory.SteelBorder, 8);
                    var box = new VBoxContainer();
                    box.AddChild(new Label
                    {
                        Text = $"[{dip.TargetNationId}] {dip.TargetNationName} | Opinião: {dip.Opinion:+0;-0;0} | Poder: {dip.EstimatedPower:F0}" +
                               (dip.AtWar ? $" | GUERRA ({dip.WarScore:F0}%)" : string.Empty),
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
                    var acts = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    acts.AddThemeConstantOverride("h_separation", 6);
                    acts.AddThemeConstantOverride("v_separation", 4);
                    var target = dip.TargetNationId;
                    AddMiniButton(acts, _loc.Get(l, "diplo.improve"), () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, DiplomaticActionKind.ImproveRelations)));
                    AddMiniButton(acts, _loc.Get(l, "diplo.trade"), () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, DiplomaticActionKind.ToggleTradePact)));
                    AddMiniButton(acts, _loc.Get(l, "diplo.alliance"), () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, DiplomaticActionKind.ToggleAlliance)));
                    AddMiniButton(acts, dip.AtWar ? _loc.Get(l, "diplo.peace") : _loc.Get(l, "diplo.war"),
                        () => CommandIssued?.Invoke(new DiplomaticActionCommand(s.PlayerNationId, target, dip.AtWar ? DiplomaticActionKind.MakePeace : DiplomaticActionKind.DeclareWar)));
                    box.AddChild(acts);
                    card.AddChild(box);
                    _leftDrawerBody.AddChild(card);
                }
                break;

            case ActiveDrawerTab.Journal:
                foreach (var je in s.PlayerJournalEntries)
                {
                    var card = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, je.Completed ? UiThemeFactory.EmeraldPositive : UiThemeFactory.SteelBorder, 10);
                    card.AddChild(new Label
                    {
                        Text = $"{_loc.Get(l, je.NameKey)} ({(je.Completed ? "OK" : $"{je.ProgressRatio * 100:F0}%")})\n{_loc.Get(l, je.DescriptionKey)}\n+{je.RewardGold:F0} {goldLabel}",
                        AutowrapMode = TextServer.AutowrapMode.WordSmart
                    });
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
        _stateMetaLabel.Text =
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
            var pBtn = new Button
            {
                Text = $"{badge}{prov.Name} (T{prov.HoldingLevel}){occBadge}",
                Modulate = isSel ? UiThemeFactory.GoldAccent : Colors.White,
                CustomMinimumSize = new Vector2(0, 28)
            };
            var capturedProv = prov;
            pBtn.Pressed += () => SelectProvince(capturedProv.ProvinceId, capturedProv.StateId);
            _provinceSelectorFlow.AddChild(pBtn);
        }

        UiThemeFactory.ClearChildren(_activeProvinceBody);
        if (activeProv is not null)
        {
            var pTitle = UiThemeFactory.CreateHeader(
                $"{(activeProv.IsCapital ? "★ " : string.Empty)}{activeProv.Name}" + (activeProv.IsCoastal ? "  ⚓" : string.Empty),
                15,
                UiThemeFactory.GoldAccent);
            _activeProvinceBody.AddChild(pTitle);

            var terrainLabel = _loc.Get(l, $"terrain.{activeProv.Terrain}");
            var hubLabel = _loc.Get(l, $"hub.{activeProv.HubType}");
            var detailText =
                $"{hubLabel}  •  {terrainLabel}" +
                (activeProv.ResourcePotential is not null ? $"  •  {activeProv.ResourcePotential}" : string.Empty) +
                $"\n{_loc.Get(l, "province.holding")}: {activeProv.HoldingLevel}/10   |   {_loc.Get(l, "province.fort")}: {activeProv.FortLevel}/5";
            if (activeProv.OccupiedByNationId is not null)
                detailText += $"\n{_loc.Get(l, "province.occupied_by")}: {activeProv.OccupiedByNationId}";

            var infoLbl = new Label { Text = detailText, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            infoLbl.AddThemeFontSizeOverride("font_size", 13);
            if (activeProv.OccupiedByNationId is not null)
                infoLbl.AddThemeColorOverride("font_color", UiThemeFactory.CrimsonAlert);
            _activeProvinceBody.AddChild(infoLbl);

            if (isPlayerState)
            {
                var provActions = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                provActions.AddThemeConstantOverride("h_separation", 8);
                provActions.AddThemeConstantOverride("v_separation", 6);
                var holdingCost = 220 + activeProv.HoldingLevel * 90;
                var fortCost = 180 + activeProv.FortLevel * 110;
                var pId = activeProv.ProvinceId;

                var holdBtn = new Button
                {
                    Text = $"{_loc.Get(l, "province.expand_holding")} ({holdingCost} {goldLabel})",
                    Disabled = activeProv.HoldingLevel >= 10
                };
                holdBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeProvinceHoldingCommand(pId, "holding"));
                provActions.AddChild(holdBtn);

                var fortBtn = new Button
                {
                    Text = $"{_loc.Get(l, "province.fortify")} ({fortCost} {goldLabel})",
                    Disabled = activeProv.FortLevel >= 5
                };
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
            var expandBtn = new Button { Text = "+1", Disabled = !isPlayerState, CustomMinimumSize = new Vector2(44, 28) };
            var bid = b.BuildingId;
            expandBtn.Pressed += () => CommandIssued?.Invoke(new BuildStructureCommand(st.StateId, bid, 1));
            topRow.AddChild(expandBtn);
            bv.AddChild(topRow);

            var pmFlow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pmFlow.AddThemeConstantOverride("h_separation", 6);
            pmFlow.AddThemeConstantOverride("v_separation", 4);
            foreach (var pm in b.AvailablePms)
            {
                var pmBtn = new Button
                {
                    Text = _loc.Get(l, pm.NameKey),
                    Disabled = !isPlayerState || !pm.Unlocked || pm.PmId == b.ActivePmId,
                    Modulate = pm.PmId == b.ActivePmId ? UiThemeFactory.GoldAccent : Colors.White
                };
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
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 34), Alignment = HorizontalAlignment.Left };
        b.Pressed += onPressed;
        _leftDrawerBody.AddChild(b);
    }

    private static void AddMiniButton(Control parent, string text, Action onPressed)
    {
        var b = new Button { Text = text };
        b.Pressed += onPressed;
        parent.AddChild(b);
    }
}
