using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Slim EU5 + 2–5% CK3 Realm Drawer backed by Scenes/UI/RealmDrawer.tscn.
/// </summary>
public sealed partial class RealmDrawerView : VBoxContainer
{
    [Export] public Label DynastyLabel { get; set; } = null!;
    [Export] public Label StatsLabel { get; set; } = null!;
    [Export] public Label TaxLabel { get; set; } = null!;
    [Export] public HSlider TaxSlider { get; set; } = null!;
    [Export] public Label CourtLabel { get; set; } = null!;
    [Export] public HSlider CourtSlider { get; set; } = null!;
    [Export] public Label EstatesHeader { get; set; } = null!;
    [Export] public VBoxContainer EstatesListBox { get; set; } = null!;

    private LocalizationCatalog? _loc;
    private string _currentNationId = "AET";
    private bool _updatingUi;

    public event Action<GameCommand>? CommandIssued;

    public void Initialize(LocalizationCatalog loc) => _loc = loc;

    public override void _Ready()
    {
        DynastyLabel ??= GetNode<Label>("RealmCard/RealmVBox/DynastyLabel");
        StatsLabel ??= GetNode<Label>("RealmCard/RealmVBox/StatsLabel");
        TaxLabel ??= GetNode<Label>("TaxLabel");
        TaxSlider ??= GetNode<HSlider>("TaxSlider");
        CourtLabel ??= GetNode<Label>("CourtLabel");
        CourtSlider ??= GetNode<HSlider>("CourtSlider");
        EstatesHeader ??= GetNode<Label>("EstatesHeader");
        EstatesListBox ??= GetNode<VBoxContainer>("EstatesListBox");

        TaxSlider.ValueChanged += val =>
        {
            if (!_updatingUi)
                CommandIssued?.Invoke(new SetTaxRateCommand(_currentNationId, val));
        };
        CourtSlider.ValueChanged += val =>
        {
            if (!_updatingUi)
                CommandIssued?.Invoke(new SetCourtSpendingCommand(_currentNationId, val));
        };
    }

    public void Render(RenderSnapshot snap, string lang)
    {
        if (_loc is null || DynastyLabel is null)
            return;

        var r = snap.Realm;
        _currentNationId = r.NationId;

        DynastyLabel.Text =
            $"{r.DynastyName}\n" +
            $"{_loc.Get(lang, "hud.ruler")}: {r.RulerName}\n" +
            $"{_loc.Get(lang, "hud.ancestry")}: {r.RulerAncestryDisplay}\n" +
            $"{_loc.Get(lang, "hud.heir")}: {r.HeirName}\n" +
            $"{_loc.Get(lang, "hud.traits")}: {(r.RulerTraits.Count > 0 ? string.Join(", ", r.RulerTraits) : "-")}";

        StatsLabel.Text =
            $"{_loc.Get(lang, "hud.gov_power")}: {r.GovernmentPower:F1}/100  ·  " +
            $"{_loc.Get(lang, "hud.stability")}: {r.Stability:F0}  ·  Prestige: {r.Prestige:F0}";

        TaxLabel.Text = $"{_loc.Get(lang, "drawer.tax_rate")} {snap.TaxRate * 100.0:F0}%";
        CourtLabel.Text = $"{_loc.Get(lang, "hud.court_cost")}: {r.CostOfCourtPercent:F0}%";

        _updatingUi = true;
        TaxSlider.Value = snap.TaxRate;
        CourtSlider.Value = r.CostOfCourtPercent;
        _updatingUi = false;

        EstatesHeader.Text = _loc.Get(lang, "drawer.estates_title");
        UiThemeFactory.ClearChildren(EstatesListBox);

        foreach (var es in snap.Estates)
        {
            var row = UiThemeFactory.CreatePill();
            var h = new HBoxContainer();
            h.AddChild(new Label
            {
                Text = _loc.Get(lang, $"estate.{es.Estate.ToString().ToLowerInvariant()}"),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            });
            h.AddChild(new Label
            {
                Text = $"{es.TaxBaseSharePercent:F1}%  (+{es.MonthlyTaxYield.ToGold():F1}g)",
                ThemeTypeVariation = "HeaderLabel"
            });
            row.AddChild(h);
            EstatesListBox.AddChild(row);
        }
    }
}
