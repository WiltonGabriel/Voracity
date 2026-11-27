using Godot;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

/// <summary>
/// Main Menu CanvasLayer backed by Scenes/UI/MainMenu.tscn with [Export] node bindings.
/// </summary>
public partial class MainMenuView : CanvasLayer
{
    [Export] public Control HomeScreen { get; set; } = null!;
    [Export] public Control NationSelectScreen { get; set; } = null!;
    [Export] public Control LoadGameScreen { get; set; } = null!;
    [Export] public Control ModsScreen { get; set; } = null!;
    [Export] public Label SubtitleLabel { get; set; } = null!;
    [Export] public Button NewGameButton { get; set; } = null!;
    [Export] public Button ContinueButton { get; set; } = null!;
    [Export] public Button MapEditorButton { get; set; } = null!;
    [Export] public Button SettingsButton { get; set; } = null!;
    [Export] public Button ModsButton { get; set; } = null!;
    [Export] public Button QuitButton { get; set; } = null!;
    [Export] public Label NationHeaderLabel { get; set; } = null!;
    [Export] public VBoxContainer NationListBox { get; set; } = null!;
    [Export] public ColorRect NationBannerBar { get; set; } = null!;
    [Export] public TextureRect NationMiniMap { get; set; } = null!;
    [Export] public Label NationDetailLabel { get; set; } = null!;
    [Export] public Button StartCampaignButton { get; set; } = null!;
    [Export] public Button NationBackButton { get; set; } = null!;
    [Export] public Label LoadHeaderLabel { get; set; } = null!;
    [Export] public VBoxContainer SaveListBox { get; set; } = null!;
    [Export] public Button LoadBackButton { get; set; } = null!;
    [Export] public Label ModsHeaderLabel { get; set; } = null!;
    [Export] public VBoxContainer ModsListBox { get; set; } = null!;
    [Export] public Button ModsBackButton { get; set; } = null!;
    [Export] public SettingsModal SettingsModal { get; set; } = null!;

    private WorldDatabase? _db;
    private GameSettings? _settings;
    private string _selectedNationId = "AET";
    private BakedMapArtifact? _previewBaked;
    private readonly Dictionary<int, string> _previewProvOwner = new();
    private readonly Dictionary<string, string> _previewNationColors = new(StringComparer.Ordinal);

    public event Action<string>? StartNewCampaignRequested;
    public event Action<string>? LoadCampaignRequested;
    public event Action? MapEditorRequested;
    public event Action<GameSettings>? SettingsChanged;

    public void Initialize(WorldDatabase db, GameSettings initialSettings)
    {
        _db = db;
        _settings = initialSettings;
        SettingsModal.Initialize(db.Localization, initialSettings);
        ShowScreen(HomeScreen);
        RefreshLocalizedTexts();
    }

    public override void _Ready()
    {
        HomeScreen ??= GetNode<Control>("Root/HomeScreen");
        NationSelectScreen ??= GetNode<Control>("Root/NationSelectScreen");
        LoadGameScreen ??= GetNode<Control>("Root/LoadGameScreen");
        ModsScreen ??= GetNode<Control>("Root/ModsScreen");
        SubtitleLabel ??= GetNode<Label>("Root/HomeScreen/Center/Card/VBox/SubtitleLabel");
        NewGameButton ??= GetNode<Button>("Root/HomeScreen/Center/Card/VBox/NewGameBtn");
        ContinueButton ??= GetNode<Button>("Root/HomeScreen/Center/Card/VBox/ContinueBtn");
        MapEditorButton ??= GetNode<Button>("Root/HomeScreen/Center/Card/VBox/MapEditorBtn");
        SettingsButton ??= GetNode<Button>("Root/HomeScreen/Center/Card/VBox/SettingsBtn");
        ModsButton ??= GetNode<Button>("Root/HomeScreen/Center/Card/VBox/ModsBtn");
        QuitButton ??= GetNode<Button>("Root/HomeScreen/Center/Card/VBox/QuitBtn");
        NationHeaderLabel ??= GetNode<Label>("Root/NationSelectScreen/Margin/Panel/VBox/NationHeader");
        NationListBox ??= GetNode<VBoxContainer>("Root/NationSelectScreen/Margin/Panel/VBox/Split/NationScroll/NationList");
        NationBannerBar ??= GetNode<ColorRect>("Root/NationSelectScreen/Margin/Panel/VBox/Split/DetailPanel/DetailVBox/BannerBar");
        NationMiniMap ??= GetNode<TextureRect>("Root/NationSelectScreen/Margin/Panel/VBox/Split/DetailPanel/DetailVBox/PreviewFrame/NationMiniMap");
        NationDetailLabel ??= GetNode<Label>("Root/NationSelectScreen/Margin/Panel/VBox/Split/DetailPanel/DetailVBox/DetailLabel");
        StartCampaignButton ??= GetNode<Button>("Root/NationSelectScreen/Margin/Panel/VBox/Footer/StartCampaignBtn");
        NationBackButton ??= GetNode<Button>("Root/NationSelectScreen/Margin/Panel/VBox/Footer/NationBackBtn");
        LoadHeaderLabel ??= GetNode<Label>("Root/LoadGameScreen/Center/Panel/VBox/LoadHeader");
        SaveListBox ??= GetNode<VBoxContainer>("Root/LoadGameScreen/Center/Panel/VBox/Scroll/SaveList");
        LoadBackButton ??= GetNode<Button>("Root/LoadGameScreen/Center/Panel/VBox/LoadBackBtn");
        ModsHeaderLabel ??= GetNode<Label>("Root/ModsScreen/Center/Panel/VBox/ModsHeader");
        ModsListBox ??= GetNode<VBoxContainer>("Root/ModsScreen/Center/Panel/VBox/Scroll/ModsList");
        ModsBackButton ??= GetNode<Button>("Root/ModsScreen/Center/Panel/VBox/ModsBackBtn");
        SettingsModal ??= GetNode<SettingsModal>("Root/SettingsModal");

        NewGameButton.Pressed += () =>
        {
            PopulateNations();
            ShowScreen(NationSelectScreen);
        };
        ContinueButton.Pressed += () =>
        {
            PopulateSaves();
            ShowScreen(LoadGameScreen);
        };
        MapEditorButton.Pressed += () => MapEditorRequested?.Invoke();
        SettingsButton.Pressed += () =>
        {
            if (_settings is not null)
                SettingsModal.Open(_settings);
        };
        ModsButton.Pressed += () =>
        {
            PopulateMods();
            ShowScreen(ModsScreen);
        };
        QuitButton.Pressed += () => GetTree().Quit();

        NationBackButton.Pressed += () => ShowScreen(HomeScreen);
        StartCampaignButton.Pressed += () => StartNewCampaignRequested?.Invoke(_selectedNationId);
        LoadBackButton.Pressed += () => ShowScreen(HomeScreen);
        ModsBackButton.Pressed += () => ShowScreen(HomeScreen);

        SettingsModal.SettingsApplied += s =>
        {
            _settings = s;
            RefreshLocalizedTexts();
            SettingsChanged?.Invoke(s);
        };

        SettingsModal.Visible = false;
        ShowScreen(HomeScreen);
        if (_db is not null && _settings is not null)
            RefreshLocalizedTexts();
    }

    public void RefreshOnShow(GameSettings latest)
    {
        _settings = latest;
        _previewBaked = null;
        ShowScreen(HomeScreen);
        RefreshLocalizedTexts();
    }

    private void PopulateNations()
    {
        if (_db is null)
            return;

        UiThemeFactory.ClearChildren(NationListBox);
        var summaries = SimulationWorld.BuildStartingNationSummaries(_db);

        foreach (var n in summaries)
        {
            var captured = n;
            var btn = new Button
            {
                Text = $"[{n.Id}] {n.Name}  ({n.RankTier})",
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(0, 38),
                Modulate = n.Id == _selectedNationId ? UiThemeFactory.GoldAccent : Colors.White
            };
            btn.SetMeta("nation_id", n.Id);
            btn.Pressed += () =>
            {
                _selectedNationId = captured.Id;
                foreach (var child in NationListBox.GetChildren())
                {
                    if (child is Button nb)
                        nb.Modulate = nb.GetMeta("nation_id").AsString() == _selectedNationId ? UiThemeFactory.GoldAccent : Colors.White;
                }
                ShowNationDetail(captured);
            };
            NationListBox.AddChild(btn);

            if (n.Id == _selectedNationId)
                ShowNationDetail(n);
        }
    }

    private void ShowNationDetail(NationSummarySnapshot n)
    {
        if (_db is null || _settings is null)
            return;

        var lang = _settings.ResolveActiveLanguage();
        var treasury = MonetaryAmount.FromGold(n.StartingTreasuryGold).ToUiString();

        NationBannerBar.Color = Color.FromHtml(n.MapColorHex);
        RenderNationPreviewMap(n.Id);

        var ownedStates = _db.States.Where(s => s.OwnerNationId == n.Id).ToList();
        var totalProvinces = ownedStates.Sum(s => s.ProvinceDetails?.Length ?? s.Provinces.Length);
        var stateNames = string.Join(", ", ownedStates.Select(s => s.Name));

        NationDetailLabel.Text =
            $"{n.Name} [{n.Id}]  —  {n.RankTier} ({n.DynastyName})\n\n" +
            $"{_db.Localization.Get(lang, "hud.ruler")}: {n.RulerName}\n" +
            $"{_db.Localization.Get(lang, "hud.ancestry")}: {n.RulerAncestryDisplay}\n" +
            $"{_db.Localization.Get(lang, "hud.traits")}: {string.Join(", ", n.RulerTraits)}\n" +
            $"{_db.Localization.Get(lang, "menu.select_nation_states")}: {ownedStates.Count} ({stateNames})\n" +
            $"{_db.Localization.Get(lang, "menu.select_nation_provinces")}: {totalProvinces}\n" +
            $"{_db.Localization.Get(lang, "state.population")}: {n.TotalPopulation:N0}\n" +
            $"{_db.Localization.Get(lang, "hud.treasury")}: {treasury}";
    }

    private void RenderNationPreviewMap(string selectedNationId)
    {
        if (_db is null)
            return;

        if (_previewBaked is null)
        {
            _previewNationColors.Clear();
            foreach (var nat in _db.Nations)
                _previewNationColors[nat.Id] = nat.MapColorHex;

            var previewProvs = new List<ProvinceSnapshot>();
            var numIdx = 1;
            _previewProvOwner.Clear();
            foreach (var st in _db.States)
            {
                if (st.ProvinceDetails is null) continue;
                foreach (var p in st.ProvinceDetails)
                {
                    _previewProvOwner[numIdx] = st.OwnerNationId;
                    previewProvs.Add(new ProvinceSnapshot(
                        numIdx++,
                        p.Id,
                        p.Name,
                        st.Id,
                        st.OwnerNationId,
                        p.Terrain,
                        p.HubType,
                        p.IsCapital,
                        p.IsCoastal,
                        (float)p.AnchorX,
                        (float)p.AnchorY,
                        p.IsCapital ? 2 : 1,
                        p.StartingFortLevel,
                        p.ResourcePotential,
                        null));
                }
            }
            var topo = PlanarTopologyBuilder.BuildFromProvinces(previewProvs, _db.GeographyFeatures);
            _previewBaked = MapBakePipeline.BakeFromTopology(
                topo,
                previewProvs,
                _db.GeographyFeatures,
                width: 480,
                height: 280);
        }

        var w = _previewBaked.Width;
        var h = _previewBaked.Height;
        var rgbBuf = new byte[w * h * 3];
        var seaDeep = new Color(0.07f, 0.12f, 0.18f);
        var seaAbyss = new Color(0.04f, 0.06f, 0.09f);
        var wasteCol = new Color(0.11f, 0.11f, 0.15f);
        var wasteRim = new Color(0.24f, 0.25f, 0.30f);
        var selHex = _previewNationColors.GetValueOrDefault(selectedNationId, "#E0B857");
        var selCol = Color.FromHtml(selHex);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var off = y * w + x;
                var idx = _previewBaked.ProvinceIdGrid[off];
                var tId = _previewBaked.TerrainGrid[off];
                Color pxCol;
                if (idx == 0)
                {
                    if (tId >= 240)
                    {
                        var wasteBorder = x > 0 && x + 1 < w && y > 0 && y + 1 < h &&
                            (_previewBaked.TerrainGrid[off - 1] < 240 ||
                             _previewBaked.TerrainGrid[off + 1] < 240 ||
                             _previewBaked.TerrainGrid[off - w] < 240 ||
                             _previewBaked.TerrainGrid[off + w] < 240);
                        var elevW = _previewBaked.HeightCoastRgb[off * 3] / 255.0f;
                        pxCol = wasteBorder ? wasteRim : wasteCol.Lightened(elevW * 0.12f);
                    }
                    else
                    {
                        var shelf = _previewBaked.HeightCoastRgb[off * 3] / 255.0f;
                        var coastProx = _previewBaked.HeightCoastRgb[off * 3 + 1] / 255.0f;
                        var abyssBlend = Mathf.Clamp((x / (float)w - 0.80f) / 0.18f, 0f, 1f);
                        var baseSea = seaDeep.Lerp(seaAbyss, abyssBlend * 0.65f);
                        pxCol = baseSea.Lightened(shelf * 0.22f + coastProx * 0.14f);
                    }
                }
                else
                {
                    var ownerId = _previewProvOwner.GetValueOrDefault(idx, string.Empty);
                    var isOwned = ownerId == selectedNationId;
                    var nL = x > 0 ? _previewBaked.ProvinceIdGrid[off - 1] : 0;
                    var nR = x + 1 < w ? _previewBaked.ProvinceIdGrid[off + 1] : 0;
                    var nU = y > 0 ? _previewBaked.ProvinceIdGrid[off - w] : 0;
                    var nD = y + 1 < h ? _previewBaked.ProvinceIdGrid[off + w] : 0;
                    var sameProvBorder = nL != idx || nR != idx || nU != idx || nD != idx;
                    var nationEdge = sameProvBorder && (
                        _previewProvOwner.GetValueOrDefault(nL, "") != ownerId ||
                        _previewProvOwner.GetValueOrDefault(nR, "") != ownerId ||
                        _previewProvOwner.GetValueOrDefault(nU, "") != ownerId ||
                        _previewProvOwner.GetValueOrDefault(nD, "") != ownerId);

                    if (isOwned)
                    {
                        var fill = selCol.Lightened(0.10f);
                        pxCol = nationEdge ? new Color(0.98f, 0.88f, 0.42f) : (sameProvBorder ? fill.Darkened(0.16f) : fill);
                    }
                    else
                    {
                        var otherHex = _previewNationColors.GetValueOrDefault(ownerId, "#555555");
                        var muted = Color.FromHtml(otherHex).Darkened(0.32f);
                        pxCol = nationEdge ? muted.Darkened(0.45f) : (sameProvBorder ? muted.Darkened(0.14f) : muted);
                    }
                }

                rgbBuf[off * 3 + 0] = (byte)pxCol.R8;
                rgbBuf[off * 3 + 1] = (byte)pxCol.G8;
                rgbBuf[off * 3 + 2] = (byte)pxCol.B8;
            }
        }

        var img = Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgbBuf);
        NationMiniMap.Texture = ImageTexture.CreateFromImage(img);
    }

    private void PopulateSaves()
    {
        if (_db is null || _settings is null)
            return;

        UiThemeFactory.ClearChildren(SaveListBox);
        var lang = _settings.ResolveActiveLanguage();
        var saves = SaveGameStorage.ListSaves();

        if (saves.Count == 0)
        {
            SaveListBox.AddChild(new Label { Text = _db.Localization.Get(lang, "menu.no_saves") });
            return;
        }

        foreach (var save in saves)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"{save.SaveName}  |  [{save.PlayerNationId}]  |  {save.Date.ToDisplayString()}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            });
            var capturedPath = save.FilePath;
            var btn = new Button { Text = _db.Localization.Get(lang, "menu.load_save") };
            btn.Pressed += () => LoadCampaignRequested?.Invoke(capturedPath);
            row.AddChild(btn);
            SaveListBox.AddChild(row);
        }
    }

    private void PopulateMods()
    {
        if (_db is null || _settings is null)
            return;

        UiThemeFactory.ClearChildren(ModsListBox);
        var lang = _settings.ResolveActiveLanguage();
        ModsListBox.AddChild(new Label
        {
            Text = $"Base: {_db.Ancestries.Count} ancestries, {_db.Goods.Count} goods, {_db.Nations.Count} nations, {_db.States.Count} states, {_db.Buildings.Count} buildings"
        });
        ModsListBox.AddChild(new HSeparator());

        if (_db.LoadedMods.Count == 0)
        {
            ModsListBox.AddChild(new Label { Text = _db.Localization.Get(lang, "menu.mods_empty") });
            return;
        }

        foreach (var mod in _db.LoadedMods)
        {
            ModsListBox.AddChild(new Label
            {
                Text = $"[{mod.LoadOrder}] {mod.Name} ({mod.Id}) v{mod.Version}"
            });
        }
    }

    private void ShowScreen(Control target)
    {
        HomeScreen.Visible = target == HomeScreen;
        NationSelectScreen.Visible = target == NationSelectScreen;
        LoadGameScreen.Visible = target == LoadGameScreen;
        ModsScreen.Visible = target == ModsScreen;
    }

    private void RefreshLocalizedTexts()
    {
        if (_db is null || _settings is null || SubtitleLabel is null)
            return;

        var lang = _settings.ResolveActiveLanguage();
        var loc = _db.Localization;

        SubtitleLabel.Text = loc.Get(lang, "menu.subtitle");
        NewGameButton.Text = loc.Get(lang, "menu.new_game");
        ContinueButton.Text = loc.Get(lang, "menu.continue");
        MapEditorButton.Text = loc.Get(lang, "menu.map_editor");
        SettingsButton.Text = loc.Get(lang, "menu.settings");
        ModsButton.Text = $"{loc.Get(lang, "menu.mods")} ({_db.LoadedMods.Count})";
        QuitButton.Text = loc.Get(lang, "menu.quit");

        NationHeaderLabel.Text = loc.Get(lang, "menu.select_nation");
        StartCampaignButton.Text = loc.Get(lang, "menu.start_campaign");
        NationBackButton.Text = loc.Get(lang, "menu.back");

        LoadHeaderLabel.Text = loc.Get(lang, "menu.continue");
        LoadBackButton.Text = loc.Get(lang, "menu.back");

        ModsHeaderLabel.Text = loc.Get(lang, "menu.mods_title");
        ModsBackButton.Text = loc.Get(lang, "menu.back");
    }

    internal void QaOpenSettings()
    {
        if (_settings is not null)
            SettingsModal.Open(_settings);
    }

    internal void QaCloseSettingsAndOpenNationSelect()
    {
        SettingsModal.Visible = false;
        PopulateNations();
        ShowScreen(NationSelectScreen);
    }
}
