using Godot;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.UI;

/// <summary>
/// Full Main Menu screen for Voracity MVP:
/// Start New Game (Pontária 1380 D.Q. Nation Picker), Load Save, Settings (with Auto/PT/EN language), Mods Viewer, Quit.
/// </summary>
public partial class MainMenuView : CanvasLayer
{
    private readonly WorldDatabase _db;
    private readonly SettingsModal _settingsModal;
    private GameSettings _settings;

    private Control _homeScreen = null!;
    private Control _nationSelectScreen = null!;
    private Control _loadGameScreen = null!;
    private Control _modsScreen = null!;

    private Label _subtitleLabel = null!;
    private Button _newGameBtn = null!;
    private Button _continueBtn = null!;
    private Button _mapEditorBtn = null!;
    private Button _settingsBtn = null!;
    private Button _modsBtn = null!;
    private Button _quitBtn = null!;

    private Label _nationHeader = null!;
    private VBoxContainer _nationListBox = null!;
    private ColorRect _nationBannerBar = null!;
    private TextureRect _nationMiniMap = null!;
    private Label _nationDetailLabel = null!;
    private Button _startCampaignBtn = null!;
    private Button _nationBackBtn = null!;
    private string _selectedNationId = "AET";
    private BakedMapArtifact? _previewBaked;
    private readonly Dictionary<int, string> _previewProvOwner = new();
    private readonly Dictionary<string, string> _previewNationColors = new(StringComparer.Ordinal);

    private Label _loadHeader = null!;
    private VBoxContainer _saveListBox = null!;
    private Button _loadBackBtn = null!;

    private Label _modsHeader = null!;
    private VBoxContainer _modsListBox = null!;
    private Button _modsBackBtn = null!;

    public event Action<string>? StartNewCampaignRequested;
    public event Action<string>? LoadCampaignRequested;
    public event Action? MapEditorRequested;
    public event Action<GameSettings>? SettingsChanged;

    public MainMenuView(WorldDatabase db, GameSettings initialSettings)
    {
        _db = db;
        _settings = initialSettings;
        _settingsModal = new SettingsModal(db.Localization, initialSettings) { Visible = false };
    }

    public override void _Ready()
    {
        Layer = 10;

        var root = new Control();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // Dark medieval Pontária backdrop
        var bg = new ColorRect { Color = new Color(0.06f, 0.06f, 0.08f, 1.0f) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(bg);

        var accentBand = new ColorRect { Color = new Color(0.11f, 0.14f, 0.19f, 0.55f) };
        accentBand.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        accentBand.CustomMinimumSize = new Vector2(1800, 260);
        accentBand.OffsetLeft = -900;
        accentBand.OffsetRight = 900;
        root.AddChild(accentBand);

        BuildHomeScreen(root);
        BuildNationSelectScreen(root);
        BuildLoadGameScreen(root);
        BuildModsScreen(root);

        _settingsModal.SettingsApplied += s =>
        {
            _settings = s;
            RefreshLocalizedTexts();
            SettingsChanged?.Invoke(s);
        };
        root.AddChild(_settingsModal);

        ShowScreen(_homeScreen);
        RefreshLocalizedTexts();
    }

    public void RefreshOnShow(GameSettings latest)
    {
        _settings = latest;
        _previewBaked = null;
        ShowScreen(_homeScreen);
        RefreshLocalizedTexts();
    }

    private void BuildHomeScreen(Control root)
    {
        _homeScreen = new Control();
        _homeScreen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_homeScreen);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _homeScreen.AddChild(center);

        var card = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 28);
        card.CustomMinimumSize = new Vector2(480, 0);
        center.AddChild(card);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        card.AddChild(vbox);

        var title = UiThemeFactory.CreateHeader("V O R A C I T Y", 42, UiThemeFactory.GoldAccent);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        _subtitleLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Word
        };
        _subtitleLabel.AddThemeColorOverride("font_color", new Color(0.80f, 0.78f, 0.72f));
        vbox.AddChild(_subtitleLabel);

        vbox.AddChild(new HSeparator());

        _newGameBtn = new Button { CustomMinimumSize = new Vector2(0, 44) };
        _newGameBtn.Pressed += () =>
        {
            PopulateNations();
            ShowScreen(_nationSelectScreen);
        };
        vbox.AddChild(_newGameBtn);

        _continueBtn = new Button { CustomMinimumSize = new Vector2(0, 42) };
        _continueBtn.Pressed += () =>
        {
            PopulateSaves();
            ShowScreen(_loadGameScreen);
        };
        vbox.AddChild(_continueBtn);

        _mapEditorBtn = new Button { CustomMinimumSize = new Vector2(0, 42) };
        _mapEditorBtn.Pressed += () => MapEditorRequested?.Invoke();
        vbox.AddChild(_mapEditorBtn);

        _settingsBtn = new Button { CustomMinimumSize = new Vector2(0, 42) };
        _settingsBtn.Pressed += () => _settingsModal.Open(_settings);
        vbox.AddChild(_settingsBtn);

        _modsBtn = new Button { CustomMinimumSize = new Vector2(0, 40) };
        _modsBtn.Pressed += () =>
        {
            PopulateMods();
            ShowScreen(_modsScreen);
        };
        vbox.AddChild(_modsBtn);

        _quitBtn = new Button { CustomMinimumSize = new Vector2(0, 40) };
        _quitBtn.Pressed += () => GetTree().Quit();
        vbox.AddChild(_quitBtn);
    }

    private void BuildNationSelectScreen(Control root)
    {
        _nationSelectScreen = new Control { Visible = false };
        _nationSelectScreen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_nationSelectScreen);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 60);
        margin.AddThemeConstantOverride("margin_right", 60);
        margin.AddThemeConstantOverride("margin_top", 40);
        margin.AddThemeConstantOverride("margin_bottom", 40);
        _nationSelectScreen.AddChild(margin);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 20);
        margin.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        _nationHeader = UiThemeFactory.CreateHeader(string.Empty, 24);
        vbox.AddChild(_nationHeader);

        var hbox = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        hbox.AddThemeConstantOverride("separation", 20);
        vbox.AddChild(hbox);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(460, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _nationListBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _nationListBox.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_nationListBox);
        hbox.AddChild(scroll);

        var detailPanel = UiThemeFactory.CreatePanel(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 18);
        detailPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var detailVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        detailVBox.AddThemeConstantOverride("separation", 10);
        detailPanel.AddChild(detailVBox);

        _nationBannerBar = new ColorRect { CustomMinimumSize = new Vector2(0, 8), Color = UiThemeFactory.GoldAccent };
        detailVBox.AddChild(_nationBannerBar);

        var previewFrame = UiThemeFactory.CreatePanel(new Color(0.06f, 0.08f, 0.11f), UiThemeFactory.SteelBorder, 6);
        _nationMiniMap = new TextureRect
        {
            CustomMinimumSize = new Vector2(460, 268),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        previewFrame.AddChild(_nationMiniMap);
        detailVBox.AddChild(previewFrame);

        _nationDetailLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _nationDetailLabel.AddThemeFontSizeOverride("font_size", 15);
        detailVBox.AddChild(_nationDetailLabel);
        hbox.AddChild(detailPanel);

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 12);
        _nationBackBtn = new Button { CustomMinimumSize = new Vector2(160, 44) };
        _nationBackBtn.Pressed += () => ShowScreen(_homeScreen);
        footer.AddChild(_nationBackBtn);

        _startCampaignBtn = new Button { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 44) };
        _startCampaignBtn.Pressed += () => StartNewCampaignRequested?.Invoke(_selectedNationId);
        footer.AddChild(_startCampaignBtn);
        vbox.AddChild(footer);
    }

    private void BuildLoadGameScreen(Control root)
    {
        _loadGameScreen = new Control { Visible = false };
        _loadGameScreen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_loadGameScreen);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadGameScreen.AddChild(center);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 20);
        panel.CustomMinimumSize = new Vector2(640, 420);
        center.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        _loadHeader = UiThemeFactory.CreateHeader(string.Empty, 22);
        vbox.AddChild(_loadHeader);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _saveListBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_saveListBox);
        vbox.AddChild(scroll);

        _loadBackBtn = new Button { CustomMinimumSize = new Vector2(160, 40) };
        _loadBackBtn.Pressed += () => ShowScreen(_homeScreen);
        vbox.AddChild(_loadBackBtn);
    }

    private void BuildModsScreen(Control root)
    {
        _modsScreen = new Control { Visible = false };
        _modsScreen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_modsScreen);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _modsScreen.AddChild(center);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.CyanAether, 20);
        panel.CustomMinimumSize = new Vector2(640, 380);
        center.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        _modsHeader = UiThemeFactory.CreateHeader(string.Empty, 22, UiThemeFactory.CyanAether);
        vbox.AddChild(_modsHeader);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _modsListBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_modsListBox);
        vbox.AddChild(scroll);

        _modsBackBtn = new Button { CustomMinimumSize = new Vector2(160, 40) };
        _modsBackBtn.Pressed += () => ShowScreen(_homeScreen);
        vbox.AddChild(_modsBackBtn);
    }

    private void PopulateNations()
    {
        UiThemeFactory.ClearChildren(_nationListBox);
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
                foreach (var child in _nationListBox.GetChildren())
                {
                    if (child is Button nb)
                        nb.Modulate = nb.GetMeta("nation_id").AsString() == _selectedNationId ? UiThemeFactory.GoldAccent : Colors.White;
                }
                ShowNationDetail(captured);
            };
            _nationListBox.AddChild(btn);

            if (n.Id == _selectedNationId)
                ShowNationDetail(n);
        }
    }

    private void ShowNationDetail(NationSummarySnapshot n)
    {
        var lang = _settings.ResolveActiveLanguage();
        var treasury = MonetaryAmount.FromGold(n.StartingTreasuryGold).ToUiString();

        _nationBannerBar.Color = Color.FromHtml(n.MapColorHex);
        RenderNationPreviewMap(n.Id);

        var ownedStates = _db.States.Where(s => s.OwnerNationId == n.Id).ToList();
        var totalProvinces = ownedStates.Sum(s => s.ProvinceDetails?.Length ?? s.Provinces.Length);
        var stateNames = string.Join(", ", ownedStates.Select(s => s.Name));

        _nationDetailLabel.Text =
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
        _nationMiniMap.Texture = ImageTexture.CreateFromImage(img);
    }

    private void PopulateSaves()
    {
        UiThemeFactory.ClearChildren(_saveListBox);
        var lang = _settings.ResolveActiveLanguage();
        var saves = SaveGameStorage.ListSaves();

        if (saves.Count == 0)
        {
            _saveListBox.AddChild(new Label { Text = _db.Localization.Get(lang, "menu.no_saves") });
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
            _saveListBox.AddChild(row);
        }
    }

    private void PopulateMods()
    {
        UiThemeFactory.ClearChildren(_modsListBox);
        var lang = _settings.ResolveActiveLanguage();
        _modsListBox.AddChild(new Label
        {
            Text = $"Base: {_db.Ancestries.Count} ancestries, {_db.Goods.Count} goods, {_db.Nations.Count} nations, {_db.States.Count} states, {_db.Buildings.Count} buildings"
        });
        _modsListBox.AddChild(new HSeparator());

        if (_db.LoadedMods.Count == 0)
        {
            _modsListBox.AddChild(new Label { Text = _db.Localization.Get(lang, "menu.mods_empty") });
            return;
        }

        foreach (var mod in _db.LoadedMods)
        {
            _modsListBox.AddChild(new Label
            {
                Text = $"[{mod.LoadOrder}] {mod.Name} ({mod.Id}) v{mod.Version}"
            });
        }
    }

    private void ShowScreen(Control target)
    {
        _homeScreen.Visible = target == _homeScreen;
        _nationSelectScreen.Visible = target == _nationSelectScreen;
        _loadGameScreen.Visible = target == _loadGameScreen;
        _modsScreen.Visible = target == _modsScreen;
    }

    private void RefreshLocalizedTexts()
    {
        var lang = _settings.ResolveActiveLanguage();
        var loc = _db.Localization;

        _subtitleLabel.Text = loc.Get(lang, "menu.subtitle");
        _newGameBtn.Text = loc.Get(lang, "menu.new_game");
        _continueBtn.Text = loc.Get(lang, "menu.continue");
        _mapEditorBtn.Text = loc.Get(lang, "menu.map_editor");
        _settingsBtn.Text = loc.Get(lang, "menu.settings");
        _modsBtn.Text = $"{loc.Get(lang, "menu.mods")} ({_db.LoadedMods.Count})";
        _quitBtn.Text = loc.Get(lang, "menu.quit");

        _nationHeader.Text = loc.Get(lang, "menu.select_nation");
        _startCampaignBtn.Text = loc.Get(lang, "menu.start_campaign");
        _nationBackBtn.Text = loc.Get(lang, "menu.back");

        _loadHeader.Text = loc.Get(lang, "menu.continue");
        _loadBackBtn.Text = loc.Get(lang, "menu.back");

        _modsHeader.Text = loc.Get(lang, "menu.mods_title");
        _modsBackBtn.Text = loc.Get(lang, "menu.back");
    }

    internal void QaOpenSettings() => _settingsModal.Open(_settings);

    internal void QaCloseSettingsAndOpenNationSelect()
    {
        _settingsModal.Visible = false;
        PopulateNations();
        ShowScreen(_nationSelectScreen);
    }
}
