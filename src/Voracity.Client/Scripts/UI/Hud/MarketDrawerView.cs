using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Displays the 6 EU5 Regional Markets with location count, mean Market Access, and dynamic good prices (-75%..+75%).
/// </summary>
public sealed class MarketDrawerView
{
    private readonly LocalizationCatalog _loc;
    private int _selectedMarketIndex;

    public MarketDrawerView(LocalizationCatalog loc) => _loc = loc;

    public void Render(VBoxContainer container, RenderSnapshot snap, string lang, Action onRedrawRequested)
    {
        if (snap.Markets.Count == 0)
        {
            container.AddChild(new Label { Text = "No regional markets." });
            return;
        }

        _selectedMarketIndex = Math.Clamp(_selectedMarketIndex, 0, snap.Markets.Count - 1);

        var selectorRow = new HFlowContainer();
        selectorRow.AddThemeConstantOverride("h_separation", 6);
        selectorRow.AddThemeConstantOverride("v_separation", 6);
        for (var i = 0; i < snap.Markets.Count; i++)
        {
            var idx = i;
            var mkt = snap.Markets[i];
            var btn = UiThemeFactory.CreateStyledButton($"{_loc.Get(lang, mkt.NameKey)} ({mkt.LocationCount})", 130, 28, active: idx == _selectedMarketIndex);
            btn.Pressed += () =>
            {
                _selectedMarketIndex = idx;
                onRedrawRequested();
            };
            selectorRow.AddChild(btn);
        }
        container.AddChild(selectorRow);

        var activeMkt = snap.Markets[_selectedMarketIndex];
        container.AddChild(new Label
        {
            Text = $"{_loc.Get(lang, activeMkt.NameKey)}  |  Center: {activeMkt.CenterLocationId}  |  Avg Market Access: {activeMkt.AverageAccess * 100.0:F1}%",
            Modulate = UiThemeFactory.GoldAccent
        });
        container.AddChild(new HSeparator());

        foreach (var g in activeMkt.Goods.OrderByDescending(x => x.CurrentPriceGold / Math.Max(0.1, x.BasePriceGold)))
        {
            var ratioPct = ((g.CurrentPriceGold / Math.Max(0.1, g.BasePriceGold)) - 1.0) * 100.0;
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = $"{g.GoodId,-14}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            row.AddChild(new Label
            {
                Text = $"{g.CurrentPriceGold,5:F1}g ({ratioPct:+0;-0;0}%)",
                CustomMinimumSize = new Vector2(115, 0),
                Modulate = ratioPct > 15 ? UiThemeFactory.CrimsonAlert : (ratioPct < -15 ? UiThemeFactory.EmeraldPositive : Colors.White)
            });
            row.AddChild(new Label
            {
                Text = $"S:{g.Supply:F0} / D:{g.Demand:F0}",
                CustomMinimumSize = new Vector2(115, 0),
                Modulate = UiThemeFactory.MutedText
            });
            container.AddChild(row);
        }
    }
}
