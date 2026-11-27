using Godot;
using Voracity.Client.UI.Hud;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

/// <summary>
/// Thin EU5 + CK3 HUD coordinator backed by Scenes/UI/HudRoot.tscn and instanced sub-scenes.
/// </summary>
public partial class HudRoot : CanvasLayer
{
    [Export] public TopBarView TopBar { get; set; } = null!;
    [Export] public Button RealmNavButton { get; set; } = null!;
    [Export] public Button MarketsNavButton { get; set; } = null!;
    [Export] public Button PopsNavButton { get; set; } = null!;
    [Export] public PanelContainer DrawerPanel { get; set; } = null!;
    [Export] public Label DrawerTitleLabel { get; set; } = null!;
    [Export] public Button DrawerCloseButton { get; set; } = null!;
    [Export] public RealmDrawerView RealmDrawer { get; set; } = null!;
    [Export] public MarketDrawerView MarketDrawer { get; set; } = null!;
    [Export] public PopsDrawerView PopsDrawer { get; set; } = null!;
    [Export] public Label OutlinerTitleLabel { get; set; } = null!;
    [Export] public VBoxContainer OutlinerContentBox { get; set; } = null!;
    [Export] public LocationInspectorView Inspector { get; set; } = null!;
    [Export] public HoverBannerView HoverBanner { get; set; } = null!;
    [Export] public MapLensBarView LensBar { get; set; } = null!;
    [Export] public DebugOverlayPanel DebugOverlay { get; set; } = null!;
    [Export] public PauseMenuOverlay PauseOverlay { get; set; } = null!;
    [Export] public SettingsModal SettingsModal { get; set; } = null!;
    [Export] public NestedTooltipManager TooltipManager { get; set; } = null!;

    [ExportGroup("UI Motion")]
    [Export] public bool EnableUiAnimations { get; set; } = true;
    [Export(PropertyHint.Range, "0.0,0.4,0.01")] public float PanelFadeSeconds { get; set; } = 0.12f;

    private LocalizationCatalog? _loc;
    private GameSettings? _settings;
    private RenderSnapshot? _latestSnapshot;
    private string _activeTab = "None";
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.Ordinal);

    public event Action<GameCommand>? CommandIssued;
    public event Action<MapLensMode>? LensModeChanged;
    public event Action<string>? FocusStateRequested;
    public event Action? QuicksaveRequested;
    public event Action? ReturnToMainMenuRequested;
    public event Action<GameSettings>? SettingsChanged;
    public event Action<int>? DebugFastForwardDaysRequested;
    public event Action? DebugBenchmarkSpeedsRequested;
    public event Action? DebugAuditWorldRequested;

    public string? SelectedProvinceId => Inspector?.SelectedLocationId;
    public bool IsPauseMenuOpen => (PauseOverlay?.Visible ?? false) || (SettingsModal?.Visible ?? false);
    public bool IsDebugOverlayVisible => DebugOverlay?.Visible ?? false;

    public void Initialize(LocalizationCatalog loc, GameSettings settings)
    {
        _loc = loc;
        _settings = settings;
        var lang = settings.ResolveActiveLanguage();

        TopBar.Initialize(loc, lang);
        TopBar.SetDebugButtonVisible(settings.DebugMode);
        Inspector.Initialize(loc, lang);
        RealmDrawer.Initialize(loc);
        MarketDrawer.Initialize(loc);
        PopsDrawer.Initialize(loc);
        LensBar.Initialize(loc, lang);
        DebugOverlay.Initialize(loc, settings);
        PauseOverlay.Initialize(loc, lang);
        SettingsModal.Initialize(loc, settings);
        TooltipManager.SetLanguage(lang);

        SetSettings(settings);
    }

    public override void _Ready()
    {
        TopBar ??= GetNode<TopBarView>("Root/TopBar");
        RealmNavButton ??= GetNode<Button>("Root/LeftNavRail/Col/RealmBtn");
        MarketsNavButton ??= GetNode<Button>("Root/LeftNavRail/Col/MarketsBtn");
        PopsNavButton ??= GetNode<Button>("Root/LeftNavRail/Col/PopsBtn");
        DrawerPanel ??= GetNode<PanelContainer>("Root/DrawerPanel");
        DrawerTitleLabel ??= GetNode<Label>("Root/DrawerPanel/VBox/Header/DrawerTitle");
        DrawerCloseButton ??= GetNode<Button>("Root/DrawerPanel/VBox/Header/CloseBtn");
        RealmDrawer ??= GetNode<RealmDrawerView>("Root/DrawerPanel/VBox/Scroll/DrawerContent/RealmDrawer");
        MarketDrawer ??= GetNode<MarketDrawerView>("Root/DrawerPanel/VBox/Scroll/DrawerContent/MarketDrawer");
        PopsDrawer ??= GetNode<PopsDrawerView>("Root/DrawerPanel/VBox/Scroll/DrawerContent/PopsDrawer");
        OutlinerTitleLabel ??= GetNode<Label>("Root/OutlinerPanel/VBox/OutlinerTitle");
        OutlinerContentBox ??= GetNode<VBoxContainer>("Root/OutlinerPanel/VBox/Scroll/OutlinerContent");
        Inspector ??= GetNode<LocationInspectorView>("Root/LocationInspector");
        HoverBanner ??= GetNode<HoverBannerView>("Root/HoverBanner");
        LensBar ??= GetNode<MapLensBarView>("Root/MapLensBar");
        DebugOverlay ??= GetNode<DebugOverlayPanel>("Root/DebugOverlay");
        PauseOverlay ??= GetNode<PauseMenuOverlay>("Root/PauseMenu");
        SettingsModal ??= GetNode<SettingsModal>("Root/SettingsModal");
        TooltipManager ??= GetNode<NestedTooltipManager>("Root/NestedTooltipManager");

        _navButtons["Realm"] = RealmNavButton;
        _navButtons["Markets"] = MarketsNavButton;
        _navButtons["Pops"] = PopsNavButton;

        RealmNavButton.Pressed += () => ToggleLeftTab("Realm");
        MarketsNavButton.Pressed += () => ToggleLeftTab("Markets");
        PopsNavButton.Pressed += () => ToggleLeftTab("Pops");
        DrawerCloseButton.Pressed += () => QaOpenTab("None");

        TopBar.CommandIssued += cmd => CommandIssued?.Invoke(cmd);
        TopBar.ConceptClicked += (id, pos) => TooltipManager.ShowConcept(id, 0, pos);
        TopBar.DebugToggleRequested += ToggleDebugOverlay;
        TopBar.PauseMenuRequested += TogglePauseMenu;
        TopBar.RealmTabRequested += () => ToggleLeftTab("Realm");

        Inspector.CommandIssued += cmd => CommandIssued?.Invoke(cmd);
        RealmDrawer.CommandIssued += cmd => CommandIssued?.Invoke(cmd);
        LensBar.LensChanged += mode => LensModeChanged?.Invoke(mode);

        DebugOverlay.FastForwardDaysRequested += d => DebugFastForwardDaysRequested?.Invoke(d);
        DebugOverlay.BenchmarkSpeedsRequested += () => DebugBenchmarkSpeedsRequested?.Invoke();
        DebugOverlay.AuditWorldRequested += () => DebugAuditWorldRequested?.Invoke();
        DebugOverlay.CollapseRequested += () => DebugOverlay.Visible = false;

        PauseOverlay.QuicksaveRequested += () => QuicksaveRequested?.Invoke();
        PauseOverlay.SettingsRequested += () =>
        {
            if (_settings is not null)
                SettingsModal.Open(_settings);
        };
        PauseOverlay.MainMenuRequested += () => ReturnToMainMenuRequested?.Invoke();
        SettingsModal.SettingsApplied += s => SettingsChanged?.Invoke(s);

        SettingsModal.Visible = false;
        DrawerPanel.Visible = false;
        Inspector.Visible = false;
        DebugOverlay.Visible = false;
        PauseOverlay.Visible = false;

        UiMotionAnimator.BindSubtleHover(RealmNavButton);
        UiMotionAnimator.BindSubtleHover(MarketsNavButton);
        UiMotionAnimator.BindSubtleHover(PopsNavButton);
    }

    public void SetSettings(GameSettings updated)
    {
        _settings = updated;
        if (_loc is null || TopBar is null)
            return;

        var lang = updated.ResolveActiveLanguage();
        TopBar.SetLanguage(lang);
        TopBar.SetDebugButtonVisible(updated.DebugMode);
        Inspector.SetLanguage(lang);
        LensBar.SetLanguage(lang);
        PauseOverlay.SetLanguage(lang);
        TooltipManager.SetLanguage(lang);
        DebugOverlay.ApplySettings(updated);
        if (!updated.DebugMode)
            DebugOverlay.Visible = false;

        RealmNavButton.Text = _loc.Get(lang, "tab.realm");
        MarketsNavButton.Text = _loc.Get(lang, "tab.markets");
        PopsNavButton.Text = _loc.Get(lang, "tab.pops");
        OutlinerTitleLabel.Text = _loc.Get(lang, "outliner.title");

        if (_latestSnapshot is not null)
            UpdateFromSnapshot(_latestSnapshot);
    }

    public void UpdateFromSnapshot(RenderSnapshot snap)
    {
        _latestSnapshot = snap;
        TopBar.UpdateFromSnapshot(snap);
        RefreshOutliner(snap);
        Inspector.Refresh(snap);
        if (DrawerPanel.Visible)
            RefreshDrawerContent();
    }

    public void UpdateClockOnly(GameDate date, bool isPaused, int speed) =>
        TopBar?.UpdateClockOnly(date, isPaused, speed);

    public void SelectProvince(string provinceId, string stateId)
    {
        if (Inspector is null)
            return;
        var wasVisible = Inspector.Visible;
        Inspector.Select(provinceId, stateId, _latestSnapshot);
        if (EnableUiAnimations && !wasVisible && Inspector.Visible)
            UiMotionAnimator.AnimateFadeIn(Inspector, PanelFadeSeconds);
    }

    public void SetHoverProvince(ProvinceSnapshot? prov)
    {
        HoverBanner?.SetHoveredLocation(prov);
        if (prov is not null)
            DebugOverlay?.SetFocusProvince(prov);
    }

    public void SetActiveLens(MapLensMode mode) => LensBar?.SetActiveLens(mode);

    public void TogglePauseMenu()
    {
        if (SettingsModal.Visible)
        {
            SettingsModal.Visible = false;
            return;
        }

        if (PauseOverlay.Visible)
            PauseOverlay.Visible = false;
        else
            PauseOverlay.Open();
    }

    public void NotifySaveSucceeded() => PauseOverlay?.NotifySaveSucceeded();

    public void ToggleDebugOverlay()
    {
        if (DebugOverlay is not null)
            DebugOverlay.Visible = !DebugOverlay.Visible;
    }

    public void UpdateDebugTelemetry(SimTelemetryMetrics metrics, RenderSnapshot? snapshot, SimulationIntegrityReport? integrity) =>
        DebugOverlay?.UpdateTelemetry(metrics, snapshot ?? _latestSnapshot, integrity);

    public void SetDebugReportText(string text) => DebugOverlay?.SetReportText(text);

    public void QaOpenTab(string tabName)
    {
        if (string.IsNullOrEmpty(tabName) || tabName == "None")
        {
            _activeTab = "None";
            DrawerPanel.Visible = false;
            foreach (var kv in _navButtons)
                UiThemeFactory.ApplyButtonHighlight(kv.Value, false);
            return;
        }

        if (_activeTab != tabName)
            ToggleLeftTab(tabName);
    }

    public void CloseStateInspector() => Inspector?.Close();

    public string InspectLayoutBounds()
    {
        var topR = TopBar.GetGlobalRect();
        var lensR = LensBar.GetGlobalRect();
        var navMin = RealmNavButton.GetGlobalRect();
        return $"top=({topR.Position.X:F0},{topR.Position.Y:F0},{topR.Size.X:F0}x{topR.Size.Y:F0}) " +
               $"lens=({lensR.Position.X:F0},{lensR.Position.Y:F0},{lensR.Size.X:F0}x{lensR.Size.Y:F0}) " +
               $"nav=({navMin.Position.X:F0},{navMin.Position.Y:F0}) drawerVisible={DrawerPanel.Visible} inspVisible={Inspector.Visible}";
    }

    private void ToggleLeftTab(string tab)
    {
        if (_activeTab == tab && DrawerPanel.Visible)
        {
            QaOpenTab("None");
            return;
        }

        _activeTab = tab;
        var wasVisible = DrawerPanel.Visible;
        DrawerPanel.Visible = true;
        if (EnableUiAnimations && !wasVisible)
            UiMotionAnimator.AnimateFadeIn(DrawerPanel, PanelFadeSeconds);
        foreach (var kv in _navButtons)
            UiThemeFactory.ApplyButtonHighlight(kv.Value, kv.Key == tab);
        RefreshDrawerContent();
    }

    private void RefreshDrawerContent()
    {
        if (_latestSnapshot is null || _loc is null || _settings is null)
            return;

        var lang = _settings.ResolveActiveLanguage();
        RealmDrawer.Visible = _activeTab == "Realm";
        MarketDrawer.Visible = _activeTab == "Markets";
        PopsDrawer.Visible = _activeTab == "Pops";

        switch (_activeTab)
        {
            case "Realm":
                DrawerTitleLabel.Text = _loc.Get(lang, "drawer.realm_title");
                RealmDrawer.Render(_latestSnapshot, lang);
                break;
            case "Markets":
                DrawerTitleLabel.Text = _loc.Get(lang, "drawer.market_title");
                MarketDrawer.Render(_latestSnapshot, lang);
                break;
            case "Pops":
                DrawerTitleLabel.Text = _loc.Get(lang, "drawer.pops_title");
                PopsDrawer.Render(_latestSnapshot, lang);
                break;
        }
    }

    private void RefreshOutliner(RenderSnapshot snap)
    {
        UiThemeFactory.ClearChildren(OutlinerContentBox);
        foreach (var st in snap.States)
        {
            if (st.OwnerNationId != snap.Realm.NationId)
                continue;
            var sId = st.StateId;
            var btn = UiThemeFactory.CreateStyledButton(
                $"{st.Name}  ({st.AverageControl:F0}% · {st.EffectiveTaxBase:F0}tb)",
                240,
                24);
            btn.ClipText = true;
            btn.Pressed += () => FocusStateRequested?.Invoke(sId);
            OutlinerContentBox.AddChild(btn);
        }
    }
}
