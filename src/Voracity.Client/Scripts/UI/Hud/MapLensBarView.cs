using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Compact bottom-right EU5 MapLensMode switcher backed by Scenes/UI/MapLensBar.tscn.
/// </summary>
public sealed partial class MapLensBarView : PanelContainer
{
    [Export] public Button PoliticalButton { get; set; } = null!;
    [Export] public Button TerrainButton { get; set; } = null!;
    [Export] public Button RgoMarketButton { get; set; } = null!;
    [Export] public Button ControlButton { get; set; } = null!;
    [Export] public Button PopFoodButton { get; set; } = null!;

    private readonly Dictionary<MapLensMode, Button> _buttons = new();
    private LocalizationCatalog? _loc;
    private string _lang = "pt-BR";
    private MapLensMode _active = MapLensMode.Political;

    public event Action<MapLensMode>? LensChanged;

    public void Initialize(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        SetLanguage(lang);
    }

    public void SetLanguage(string lang)
    {
        _lang = lang;
        if (_loc is null || _buttons.Count == 0)
            return;

        _buttons[MapLensMode.Political].Text = _loc.Get(_lang, "lens.political");
        _buttons[MapLensMode.Terrain].Text = _loc.Get(_lang, "lens.terrain");
        _buttons[MapLensMode.RgoAndMarket].Text = _loc.Get(_lang, "lens.rgo_market");
        _buttons[MapLensMode.ControlProximity].Text = _loc.Get(_lang, "lens.control");
        _buttons[MapLensMode.PopulationFood].Text = _loc.Get(_lang, "lens.pop_food");
    }

    public override void _Ready()
    {
        PoliticalButton ??= GetNode<Button>("Row/PoliticalBtn");
        TerrainButton ??= GetNode<Button>("Row/TerrainBtn");
        RgoMarketButton ??= GetNode<Button>("Row/RgoMarketBtn");
        ControlButton ??= GetNode<Button>("Row/ControlBtn");
        PopFoodButton ??= GetNode<Button>("Row/PopFoodBtn");

        RegisterButton(MapLensMode.Political, PoliticalButton);
        RegisterButton(MapLensMode.Terrain, TerrainButton);
        RegisterButton(MapLensMode.RgoAndMarket, RgoMarketButton);
        RegisterButton(MapLensMode.ControlProximity, ControlButton);
        RegisterButton(MapLensMode.PopulationFood, PopFoodButton);

        SetActiveLens(_active);
        if (_loc is not null)
            SetLanguage(_lang);
    }

    public void SetActiveLens(MapLensMode mode)
    {
        _active = mode;
        foreach (var kv in _buttons)
            UiThemeFactory.ApplyButtonHighlight(kv.Value, kv.Key == _active);
    }

    private void RegisterButton(MapLensMode mode, Button btn)
    {
        _buttons[mode] = btn;
        btn.Pressed += () =>
        {
            SetActiveLens(mode);
            LensChanged?.Invoke(mode);
        };
    }
}
