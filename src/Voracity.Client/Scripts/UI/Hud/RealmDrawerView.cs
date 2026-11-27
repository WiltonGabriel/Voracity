using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Displays the slim EU5 + 2–5% CK3 Realm Drawer:
/// Dynasty / Ruler / Heir / Dual Ancestry / 2–3 RulerTraits, Tax Rate slider,
/// Cost of the Court slider (0..100%), and Estate Tax Base Shares hook.
/// </summary>
public sealed class RealmDrawerView
{
    private readonly LocalizationCatalog _loc;

    public RealmDrawerView(LocalizationCatalog loc) => _loc = loc;

    public void Render(VBoxContainer container, RenderSnapshot snap, string lang, Action<GameCommand> issueCommand)
    {
        var r = snap.Realm;
        var card = UiThemeFactory.CreatePill(UiThemeFactory.CardBg, UiThemeFactory.GoldAccent, 10, 8);
        var cardV = new VBoxContainer();
        card.AddChild(cardV);

        cardV.AddChild(new Label
        {
            Text = $"{r.DynastyName}\n" +
                   $"{_loc.Get(lang, "hud.ruler")}: {r.RulerName}\n" +
                   $"{_loc.Get(lang, "hud.ancestry")}: {r.RulerAncestryDisplay}\n" +
                   $"{_loc.Get(lang, "hud.heir")}: {r.HeirName}\n" +
                   $"{_loc.Get(lang, "hud.traits")}: {(r.RulerTraits.Count > 0 ? string.Join(", ", r.RulerTraits) : "-")}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        cardV.AddChild(new Label
        {
            Text = $"{_loc.Get(lang, "hud.gov_power")}: {r.GovernmentPower:F1}/100  ·  " +
                   $"{_loc.Get(lang, "hud.stability")}: {r.Stability:F0}  ·  Prestige: {r.Prestige:F0}",
            Modulate = UiThemeFactory.CyanAether
        });
        container.AddChild(card);

        // Tax Rate slider
        container.AddChild(new Label { Text = $"{_loc.Get(lang, "drawer.tax_rate")} {snap.TaxRate * 100.0:F0}%" });
        var taxSlider = new HSlider
        {
            MinValue = 0.0,
            MaxValue = 0.50,
            Step = 0.05,
            Value = snap.TaxRate
        };
        taxSlider.ValueChanged += val => issueCommand(new SetTaxRateCommand(r.NationId, val));
        container.AddChild(taxSlider);

        // Cost of the Court slider (EU5 Economy.md: 10% of Tax Base at 100% slider, +0.02 GovPower)
        container.AddChild(new Label { Text = $"{_loc.Get(lang, "hud.court_cost")}: {r.CostOfCourtPercent:F0}%" });
        var courtSlider = new HSlider
        {
            MinValue = 0.0,
            MaxValue = 100.0,
            Step = 5.0,
            Value = r.CostOfCourtPercent
        };
        courtSlider.ValueChanged += val => issueCommand(new SetCourtSpendingCommand(r.NationId, val));
        container.AddChild(courtSlider);

        container.AddChild(new HSeparator());
        container.AddChild(UiThemeFactory.CreateHeader(_loc.Get(lang, "drawer.estates_title"), 13));

        foreach (var es in snap.Estates)
        {
            var row = UiThemeFactory.CreatePill(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 8, 5);
            var h = new HBoxContainer();
            h.AddChild(new Label
            {
                Text = _loc.Get(lang, $"estate.{es.Estate.ToString().ToLowerInvariant()}"),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            });
            h.AddChild(new Label
            {
                Text = $"{es.TaxBaseSharePercent:F1}%  (+{es.MonthlyTaxYield.ToGold():F1}g)",
                Modulate = UiThemeFactory.GoldAccent
            });
            row.AddChild(h);
            container.AddChild(row);
        }
    }
}
