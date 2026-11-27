using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Displays the 8 EU5 Social Classes summary and Dual Ancestry (50%/50%) demographic breakdown.
/// </summary>
public sealed class PopsDrawerView
{
    private readonly LocalizationCatalog _loc;

    public PopsDrawerView(LocalizationCatalog loc) => _loc = loc;

    public void Render(VBoxContainer container, RenderSnapshot snap, string lang)
    {
        container.AddChild(new Label
        {
            Text = $"{_loc.Get(lang, "hud.world_pop")}: {snap.PlayerPopulation:N0} ({snap.ActivePopCount} world groups)",
            Modulate = UiThemeFactory.GoldAccent
        });
        container.AddChild(new HSeparator());

        var byClass = new Dictionary<SocialClass, long>();
        foreach (var p in snap.PlayerPops)
            byClass[p.SocialClass] = byClass.GetValueOrDefault(p.SocialClass) + p.Population;

        foreach (SocialClass cls in Enum.GetValues<SocialClass>())
        {
            var count = byClass.GetValueOrDefault(cls, 0);
            if (count <= 0)
                continue;
            var share = snap.PlayerPopulation > 0 ? count * 100.0 / snap.PlayerPopulation : 0.0;
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = _loc.Get(lang, $"class.{cls.ToString().ToLowerInvariant()}"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            row.AddChild(new Label { Text = $"{count:N0} ({share:F1}%)", CustomMinimumSize = new Vector2(150, 0) });
            container.AddChild(row);
        }

        container.AddChild(new HSeparator());
        foreach (var pop in snap.PlayerPops.OrderByDescending(p => p.Population).Take(25))
        {
            var row = UiThemeFactory.CreatePill(UiThemeFactory.CardBg, UiThemeFactory.SteelBorder, 8, 4);
            var h = new HBoxContainer();
            h.AddChild(new Label
            {
                Text = $"{pop.LocationName} · {pop.SocialClass} [{pop.AncestryDisplay}]",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            });
            h.AddChild(new Label { Text = $"{pop.Population:N0} (Lit {pop.Literacy * 100:F0}%)" });
            row.AddChild(h);
            container.AddChild(row);
        }
    }
}
