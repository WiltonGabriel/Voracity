using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Compact bottom-right bar switching the 5 EU5 MapLensModes:
/// Political, Terrain, RgoAndMarket, ControlProximity, PopulationFood.
/// </summary>
public sealed partial class MapLensBarView : PanelContainer
{
    private readonly LocalizationCatalog _loc;
    private string _lang = "pt-BR";
    private readonly Dictionary<MapLensMode, Button> _buttons = new();
    private MapLensMode _active = MapLensMode.Political;

    public event Action<MapLensMode>? LensChanged;

    public MapLensBarView(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        _lang = lang;
    }

    public void SetLanguage(string lang)
    {
        _lang = lang;
        if (_buttons.Count == 0)
            return;
        _buttons[MapLensMode.Political].Text = _loc.Get(_lang, "lens.political");
        _buttons[MapLensMode.Terrain].Text = _loc.Get(_lang, "lens.terrain");
        _buttons[MapLensMode.RgoAndMarket].Text = _loc.Get(_lang, "lens.rgo_market");
        _buttons[MapLensMode.ControlProximity].Text = _loc.Get(_lang, "lens.control");
        _buttons[MapLensMode.PopulationFood].Text = _loc.Get(_lang, "lens.pop_food");
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.BottomRight);
        OffsetLeft = -620;
        OffsetRight = -10;
        OffsetTop = -54;
        OffsetBottom = -10;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiThemeFactory.CreateBox(UiThemeFactory.PanelBg, UiThemeFactory.SteelBorder, 6));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        AddChild(row);

        AddLensButton(row, MapLensMode.Political, "lens.political");
        AddLensButton(row, MapLensMode.Terrain, "lens.terrain");
        AddLensButton(row, MapLensMode.RgoAndMarket, "lens.rgo_market");
        AddLensButton(row, MapLensMode.ControlProximity, "lens.control");
        AddLensButton(row, MapLensMode.PopulationFood, "lens.pop_food");
        SetActiveLens(MapLensMode.Political);
    }

    public void SetActiveLens(MapLensMode mode)
    {
        _active = mode;
        foreach (var kv in _buttons)
            UiThemeFactory.ApplyButtonHighlight(kv.Value, kv.Key == _active);
    }

    private void AddLensButton(HBoxContainer row, MapLensMode mode, string key)
    {
        var btn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, key), 108, 30);
        btn.Pressed += () =>
        {
            SetActiveLens(mode);
            LensChanged?.Invoke(mode);
        };
        _buttons[mode] = btn;
        row.AddChild(btn);
    }
}
