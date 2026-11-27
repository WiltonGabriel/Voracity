using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// EU5 Location &amp; Province Inspector (right-bottom side panel):
/// Renders Rank/Topography/Vegetation/Climate/Harbor, Control/Proximity, RGO + Expand button,
/// 3-Layer Food balance, Market Access, Potential/Effective Tax Base, Buildings &amp; PM switcher,
/// and dedicated SeaZone inspection mode when a maritime zone is clicked.
/// </summary>
public sealed partial class LocationInspectorView : PanelContainer
{
    private readonly LocalizationCatalog _loc;
    private string _lang = "pt-BR";

    private Label _titleLabel = null!;
    private ColorRect _terrainBanner = null!;
    private Label _terrainMetaLabel = null!;
    private VBoxContainer _contentBox = null!;

    public string? SelectedLocationId { get; private set; }
    public string? SelectedStateId { get; private set; }

    public event Action<GameCommand>? CommandIssued;
    public event Action? Closed;

    public LocationInspectorView(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        _lang = lang;
    }

    public void SetLanguage(string lang) => _lang = lang;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -410;
        OffsetRight = -10;
        OffsetTop = 278;
        OffsetBottom = -168;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiThemeFactory.CreateBox(UiThemeFactory.PanelBg, UiThemeFactory.BronzeAccent, 10));

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        var header = new HBoxContainer();
        _titleLabel = UiThemeFactory.CreateHeader("Location", 15);
        _titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_titleLabel);

        var closeBtn = UiThemeFactory.CreateStyledButton("X", 28, 26);
        closeBtn.Pressed += () =>
        {
            Close();
            Closed?.Invoke();
        };
        header.AddChild(closeBtn);
        vbox.AddChild(header);

        var bannerPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, 32) };
        _terrainBanner = new ColorRect { Color = new Color(0.25f, 0.30f, 0.22f, 0.9f) };
        _terrainBanner.SetAnchorsPreset(LayoutPreset.FullRect);
        bannerPanel.AddChild(_terrainBanner);

        _terrainMetaLabel = new Label
        {
            Text = string.Empty,
            VerticalAlignment = VerticalAlignment.Center
        };
        _terrainMetaLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        _terrainMetaLabel.AddThemeFontSizeOverride("font_size", 11);
        _terrainMetaLabel.AddThemeColorOverride("font_color", Colors.White);
        bannerPanel.AddChild(_terrainMetaLabel);
        vbox.AddChild(bannerPanel);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _contentBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentBox.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_contentBox);
        vbox.AddChild(scroll);
    }

    public void Select(string locId, string stateId, RenderSnapshot? snap)
    {
        SelectedLocationId = locId;
        SelectedStateId = stateId;
        Visible = true;
        if (snap is not null)
            Refresh(snap);
    }

    public void Close()
    {
        Visible = false;
        SelectedLocationId = null;
    }

    public void Refresh(RenderSnapshot snap)
    {
        if (!Visible || SelectedLocationId is null)
            return;

        var loc = snap.Provinces.FirstOrDefault(p => p.ProvinceId == SelectedLocationId);
        if (loc is null)
            return;

        var display = loc.DynamicDisplayName ?? loc.Name;
        _titleLabel.Text = $"{display} ({loc.Kind})";
        _terrainBanner.Color = loc.Kind == LocationKind.SeaZone
            ? new Color(0.12f, 0.24f, 0.34f, 0.95f)
            : UiThemeFactory.GetTerrainBannerColor(loc.Terrain);

        UiThemeFactory.ClearChildren(_contentBox);

        if (loc.Kind == LocationKind.SeaZone)
        {
            _terrainMetaLabel.Text = $"  {loc.Topography} · {loc.Climate} · ID #{loc.NumericalIndex}";
            _contentBox.AddChild(new Label
            {
                Text = $"{_loc.Get(_lang, "loc.maritime_presence")}: {loc.SeaZoneMaritimePresence * 100.0:F0}%",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            });
            return;
        }

        var st = snap.States.FirstOrDefault(s => s.StateId == loc.StateId);
        var provFood = snap.ProvinceFoods.FirstOrDefault(f => f.AreaId == loc.StateId);
        var coastalTag = loc.IsCoastal ? $"Harbor {loc.NaturalHarborSuitability}%" : "Inland";
        _terrainMetaLabel.Text = $"  {loc.Rank} · {loc.Topography}/{loc.Vegetation} · {coastalTag}";

        // Control & Proximity + Road
        AddInfoSection(
            $"{_loc.Get(_lang, "hud.control")}: {loc.Control:F1}% / {loc.MaxControl:F1}%  |  {_loc.Get(_lang, "hud.proximity")}: {loc.Proximity:F1}%",
            $"{_loc.Get(_lang, "hud.road")}: {loc.RoadLevel} · Dev: {loc.Development:F1} · {_loc.Get(_lang, "hud.integration")}: {loc.IntegrationStatus}");

        // Economy: Tax Base & RGO
        var rgoGood = loc.ResourcePotential ?? "grain";
        AddInfoSection(
            $"{_loc.Get(_lang, "hud.tax_base")}: {loc.EffectiveTaxBase:F2} / {loc.PotentialTaxBase:F2}",
            $"{_loc.Get(_lang, "loc.rgo")}: {rgoGood} ({loc.RgoMethod}) Lv.{loc.RgoLevel}/{loc.MaxRgoLevel} ({loc.RgoEmploymentRatio * 100.0:F0}%)");

        // Market Access & Population Capacity & Food
        var netFood = loc.LocalFoodProduced - loc.LocalFoodConsumed;
        var areaFoodStr = provFood is not null
            ? $"Area Storage: {provFood.FoodStorage:F0}/{provFood.MaxFoodStorage:F0}{(provFood.IsStarving ? " [STARVING]" : "")}"
            : string.Empty;
        AddInfoSection(
            $"{_loc.Get(_lang, "loc.market_access")}: {loc.MarketAccess * 100.0:F0}% ({loc.MarketId ?? "-"})",
            $"{_loc.Get(_lang, "state.population")}: {loc.Population:N0} / {loc.PopulationCapacity:N0}\n{_loc.Get(_lang, "hud.food")}: +{loc.LocalFoodProduced:F1} / -{loc.LocalFoodConsumed:F1} ({(netFood >= 0 ? "+" : "")}{netFood:F1})  {areaFoodStr}");

        // Player-owned Location Upgrade & Building Controls
        var isOwnedByPlayer = loc.OwnerNationId == snap.Realm.NationId;
        if (isOwnedByPlayer && loc.Kind == LocationKind.Habitable)
        {
            var actionsRow = new HBoxContainer();
            actionsRow.AddThemeConstantOverride("separation", 6);

            var rgoBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "loc.expand_rgo"), 110, 28, UiThemeFactory.EmeraldPositive);
            rgoBtn.Disabled = loc.RgoLevel >= loc.MaxRgoLevel;
            rgoBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeLocationCommand(loc.ProvinceId, LocationUpgradeKind.ExpandRgo));
            actionsRow.AddChild(rgoBtn);

            var rankBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "loc.upgrade_rank"), 110, 28, UiThemeFactory.GoldAccent);
            rankBtn.Disabled = loc.Rank >= LocationRank.Megalopolis;
            rankBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeLocationCommand(loc.ProvinceId, LocationUpgradeKind.UpgradeRank));
            actionsRow.AddChild(rankBtn);

            var roadBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "loc.upgrade_road"), 110, 28, UiThemeFactory.CyanAether);
            roadBtn.Disabled = loc.RoadLevel >= RoadLevel.ModernRoad;
            roadBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeLocationCommand(loc.ProvinceId, LocationUpgradeKind.UpgradeRoad));
            actionsRow.AddChild(roadBtn);

            _contentBox.AddChild(actionsRow);

            // Buildings & Production Methods
            if (loc.Buildings is { Count: > 0 })
            {
                _contentBox.AddChild(new HSeparator());
                _contentBox.AddChild(UiThemeFactory.CreateHeader(_loc.Get(_lang, "state.buildings"), 13));

                foreach (var b in loc.Buildings)
                {
                    var card = UiThemeFactory.CreatePill(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 8, 5);
                    var cardV = new VBoxContainer();
                    card.AddChild(cardV);

                    var topRow = new HBoxContainer();
                    topRow.AddChild(new Label
                    {
                        Text = $"{_loc.Get(_lang, b.NameKey)}  Lv.{b.Levels}",
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    });
                    var buildBtn = UiThemeFactory.CreateStyledButton("+1", 40, 24, UiThemeFactory.GoldAccent);
                    var bId = b.BuildingId;
                    buildBtn.Pressed += () => CommandIssued?.Invoke(new BuildStructureCommand(loc.ProvinceId, bId, 1));
                    topRow.AddChild(buildBtn);
                    cardV.AddChild(topRow);

                    var pmRow = new HBoxContainer();
                    pmRow.AddChild(new Label
                    {
                        Text = $"PM: {_loc.Get(_lang, b.ActivePmNameKey)}",
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        Modulate = UiThemeFactory.MutedText
                    });
                    if (b.AvailablePms.Count > 1)
                    {
                        var curIdx = 0;
                        for (var pIdx = 0; pIdx < b.AvailablePms.Count; pIdx++)
                        {
                            if (b.AvailablePms[pIdx].PmId == b.ActivePmId)
                            {
                                curIdx = pIdx;
                                break;
                            }
                        }
                        var nextPmId = b.AvailablePms[(curIdx + 1) % b.AvailablePms.Count].PmId;
                        var swBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "state.switch_pm"), 90, 24);
                        swBtn.Pressed += () => CommandIssued?.Invoke(new SwitchProductionMethodCommand(loc.ProvinceId, bId, nextPmId));
                        pmRow.AddChild(swBtn);
                    }
                    cardV.AddChild(pmRow);
                    _contentBox.AddChild(card);
                }
            }
        }
        else if (st is not null)
        {
            _contentBox.AddChild(new Label { Text = $"{st.Name} [{st.OwnerNationId}]", Modulate = UiThemeFactory.MutedText });
        }
    }

    private void AddInfoSection(string primary, string secondary)
    {
        var box = UiThemeFactory.CreatePill(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 8, 5);
        var v = new VBoxContainer();
        v.AddChild(new Label { Text = primary, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var sub = new Label { Text = secondary, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        sub.AddThemeFontSizeOverride("font_size", 11);
        sub.AddThemeColorOverride("font_color", UiThemeFactory.MutedText);
        v.AddChild(sub);
        box.AddChild(v);
        _contentBox.AddChild(box);
    }
}
