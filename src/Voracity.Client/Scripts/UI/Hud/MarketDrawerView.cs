using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Displays the 6 EU5 Regional Markets backed by Scenes/UI/MarketDrawer.tscn.
/// </summary>
public sealed partial class MarketDrawerView : VBoxContainer
{
    [Export] public HFlowContainer SelectorRow { get; set; } = null!;
    [Export] public Label SummaryLabel { get; set; } = null!;
    [Export] public VBoxContainer GoodsListBox { get; set; } = null!;

    private LocalizationCatalog? _loc;
    private int _selectedMarketIndex;
    private RenderSnapshot? _lastSnapshot;
    private string _lastLang = "pt-BR";

    public void Initialize(LocalizationCatalog loc) => _loc = loc;

    public override void _Ready()
    {
        SelectorRow ??= GetNode<HFlowContainer>("SelectorRow");
        SummaryLabel ??= GetNode<Label>("SummaryLabel");
        GoodsListBox ??= GetNode<VBoxContainer>("GoodsListBox");
    }

    public void Render(RenderSnapshot snap, string lang)
    {
        if (_loc is null || SelectorRow is null)
            return;

        _lastSnapshot = snap;
        _lastLang = lang;
        UiThemeFactory.ClearChildren(SelectorRow);
        UiThemeFactory.ClearChildren(GoodsListBox);

        if (snap.Markets.Count == 0)
        {
            SummaryLabel.Text = "No regional markets.";
            return;
        }

        _selectedMarketIndex = Math.Clamp(_selectedMarketIndex, 0, snap.Markets.Count - 1);

        for (var i = 0; i < snap.Markets.Count; i++)
        {
            var idx = i;
            var mkt = snap.Markets[i];
            var btn = UiThemeFactory.CreateStyledButton($"{_loc.Get(lang, mkt.NameKey)} ({mkt.LocationCount})", 130, 28, active: idx == _selectedMarketIndex);
            btn.Pressed += () =>
            {
                _selectedMarketIndex = idx;
                if (_lastSnapshot is not null)
                    Render(_lastSnapshot, _lastLang);
            };
            SelectorRow.AddChild(btn);
        }

        var activeMkt = snap.Markets[_selectedMarketIndex];
        SummaryLabel.Text = $"{_loc.Get(lang, activeMkt.NameKey)}  |  Center: {activeMkt.CenterLocationId}  |  Avg Market Access: {activeMkt.AverageAccess * 100.0:F1}%";

        foreach (var g in activeMkt.Goods.OrderByDescending(x => x.CurrentPriceGold / Math.Max(0.1, x.BasePriceGold)))
        {
            var ratioPct = ((g.CurrentPriceGold / Math.Max(0.1, g.BasePriceGold)) - 1.0) * 100.0;
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = $"{g.GoodId,-14}", SizeFlagsHorizontal = SizeFlags.ExpandFill });
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
                ThemeTypeVariation = "MutedLabel"
            });
            GoodsListBox.AddChild(row);
        }
    }
}
