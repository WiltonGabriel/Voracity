using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// EU5 Location &amp; Province Inspector backed by Scenes/UI/LocationInspector.tscn.
/// </summary>
public sealed partial class LocationInspectorView : PanelContainer
{
    [Export] public Label TitleLabel { get; set; } = null!;
    [Export] public Button CloseButton { get; set; } = null!;
    [Export] public ColorRect TerrainBanner { get; set; } = null!;
    [Export] public Label TerrainMetaLabel { get; set; } = null!;
    [Export] public VBoxContainer ContentBox { get; set; } = null!;

    private LocalizationCatalog? _loc;
    private string _lang = "pt-BR";

    public string? SelectedLocationId { get; private set; }
    public string? SelectedStateId { get; private set; }

    public event Action<GameCommand>? CommandIssued;
    public event Action? Closed;

    public void Initialize(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        _lang = lang;
    }

    public void SetLanguage(string lang) => _lang = lang;

    public override void _Ready()
    {
        TitleLabel ??= GetNode<Label>("VBox/Header/TitleLabel");
        CloseButton ??= GetNode<Button>("VBox/Header/CloseBtn");
        TerrainBanner ??= GetNode<ColorRect>("VBox/BannerPanel/TerrainBanner");
        TerrainMetaLabel ??= GetNode<Label>("VBox/BannerPanel/TerrainMetaLabel");
        ContentBox ??= GetNode<VBoxContainer>("VBox/Scroll/ContentBox");

        CloseButton.Pressed += () =>
        {
            Close();
            Closed?.Invoke();
        };
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
        if (!Visible || SelectedLocationId is null || _loc is null)
            return;

        var loc = snap.Provinces.FirstOrDefault(p => p.ProvinceId == SelectedLocationId);
        if (loc is null)
            return;

        var display = loc.DynamicDisplayName ?? loc.Name;
        TitleLabel.Text = $"{display} ({loc.Kind})";
        TerrainBanner.Color = loc.Kind == LocationKind.SeaZone
            ? new Color(0.12f, 0.24f, 0.34f, 0.95f)
            : UiThemeFactory.GetTerrainBannerColor(loc.Terrain);

        UiThemeFactory.ClearChildren(ContentBox);

        if (loc.Kind == LocationKind.SeaZone)
        {
            TerrainMetaLabel.Text = $"  {loc.Topography} · {loc.Climate} · ID #{loc.NumericalIndex}";
            ContentBox.AddChild(new Label
            {
                Text = $"{_loc.Get(_lang, "loc.maritime_presence")}: {loc.SeaZoneMaritimePresence * 100.0:F0}%",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            });
            return;
        }

        var st = snap.States.FirstOrDefault(s => s.StateId == loc.StateId);
        var provFood = snap.ProvinceFoods.FirstOrDefault(f => f.AreaId == loc.StateId);
        var coastalTag = loc.IsCoastal ? $"Harbor {loc.NaturalHarborSuitability}%" : "Inland";
        TerrainMetaLabel.Text = $"  {loc.Rank} · {loc.Topography}/{loc.Vegetation} · {coastalTag}";

        AddInfoSection(
            $"{_loc.Get(_lang, "hud.control")}: {loc.Control:F1}% / {loc.MaxControl:F1}%  |  {_loc.Get(_lang, "hud.proximity")}: {loc.Proximity:F1}%",
            $"{_loc.Get(_lang, "hud.road")}: {loc.RoadLevel} · Dev: {loc.Development:F1} · {_loc.Get(_lang, "hud.integration")}: {loc.IntegrationStatus}");

        var rgoGood = loc.ResourcePotential ?? "grain";
        AddInfoSection(
            $"{_loc.Get(_lang, "hud.tax_base")}: {loc.EffectiveTaxBase:F2} / {loc.PotentialTaxBase:F2}",
            $"{_loc.Get(_lang, "loc.rgo")}: {rgoGood} ({loc.RgoMethod}) Lv.{loc.RgoLevel}/{loc.MaxRgoLevel} ({loc.RgoEmploymentRatio * 100.0:F0}%)");

        var netFood = loc.LocalFoodProduced - loc.LocalFoodConsumed;
        var areaFoodStr = provFood is not null
            ? $"Area Storage: {provFood.FoodStorage:F0}/{provFood.MaxFoodStorage:F0}{(provFood.IsStarving ? " [STARVING]" : "")}"
            : string.Empty;
        AddInfoSection(
            $"{_loc.Get(_lang, "loc.market_access")}: {loc.MarketAccess * 100.0:F0}% ({loc.MarketId ?? "-"})",
            $"{_loc.Get(_lang, "state.population")}: {loc.Population:N0} / {loc.PopulationCapacity:N0}\n{_loc.Get(_lang, "hud.food")}: +{loc.LocalFoodProduced:F1} / -{loc.LocalFoodConsumed:F1} ({(netFood >= 0 ? "+" : "")}{netFood:F1})  {areaFoodStr}");

        var isOwnedByPlayer = loc.OwnerNationId == snap.Realm.NationId;
        if (isOwnedByPlayer && loc.Kind == LocationKind.Habitable)
        {
            var actionsRow = new HBoxContainer();
            actionsRow.AddThemeConstantOverride("separation", 6);

            var rgoBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "loc.expand_rgo"), 105, 28, UiThemeFactory.EmeraldPositive);
            rgoBtn.Disabled = loc.RgoLevel >= loc.MaxRgoLevel;
            rgoBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeLocationCommand(loc.ProvinceId, LocationUpgradeKind.ExpandRgo));
            actionsRow.AddChild(rgoBtn);

            var rankBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "loc.upgrade_rank"), 105, 28, UiThemeFactory.GoldAccent);
            rankBtn.Disabled = loc.Rank >= LocationRank.Megalopolis;
            rankBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeLocationCommand(loc.ProvinceId, LocationUpgradeKind.UpgradeRank));
            actionsRow.AddChild(rankBtn);

            var roadBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "loc.upgrade_road"), 105, 28, UiThemeFactory.CyanAether);
            roadBtn.Disabled = loc.RoadLevel >= RoadLevel.ModernRoad;
            roadBtn.Pressed += () => CommandIssued?.Invoke(new UpgradeLocationCommand(loc.ProvinceId, LocationUpgradeKind.UpgradeRoad));
            actionsRow.AddChild(roadBtn);

            ContentBox.AddChild(actionsRow);

            if (loc.Buildings is { Count: > 0 })
            {
                ContentBox.AddChild(new HSeparator());
                ContentBox.AddChild(UiThemeFactory.CreateHeader(_loc.Get(_lang, "state.buildings"), 13));

                foreach (var b in loc.Buildings)
                {
                    var card = UiThemeFactory.CreatePill();
                    var cardV = new VBoxContainer();
                    card.AddChild(cardV);

                    var topRow = new HBoxContainer();
                    topRow.AddChild(new Label
                    {
                        Text = $"{_loc.Get(_lang, b.NameKey)}  Lv.{b.Levels}",
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    });
                    var buildBtn = UiThemeFactory.CreateStyledButton("+1", 40, 24, active: true);
                    var bId = b.BuildingId;
                    buildBtn.Pressed += () => CommandIssued?.Invoke(new BuildStructureCommand(loc.ProvinceId, bId, 1));
                    topRow.AddChild(buildBtn);
                    cardV.AddChild(topRow);

                    var pmRow = new HBoxContainer();
                    pmRow.AddChild(new Label
                    {
                        Text = $"PM: {_loc.Get(_lang, b.ActivePmNameKey)}",
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        ThemeTypeVariation = "MutedLabel"
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
                        var swBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "state.switch_pm"), 88, 24);
                        swBtn.Pressed += () => CommandIssued?.Invoke(new SwitchProductionMethodCommand(loc.ProvinceId, bId, nextPmId));
                        pmRow.AddChild(swBtn);
                    }
                    cardV.AddChild(pmRow);
                    ContentBox.AddChild(card);
                }
            }
        }
        else if (st is not null)
        {
            ContentBox.AddChild(new Label { Text = $"{st.Name} [{st.OwnerNationId}]", ThemeTypeVariation = "MutedLabel" });
        }
    }

    private void AddInfoSection(string primary, string secondary)
    {
        var box = UiThemeFactory.CreatePill();
        var v = new VBoxContainer();
        v.AddChild(new Label { Text = primary, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var sub = new Label
        {
            Text = secondary,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ThemeTypeVariation = "MutedLabel"
        };
        v.AddChild(sub);
        box.AddChild(v);
        ContentBox.AddChild(box);
    }
}
