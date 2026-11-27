using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Displays the 8 EU5 Social Classes summary and Dual Ancestry (50%/50%) backed by Scenes/UI/PopsDrawer.tscn.
/// </summary>
public sealed partial class PopsDrawerView : VBoxContainer
{
    [Export] public Label SummaryLabel { get; set; } = null!;
    [Export] public VBoxContainer ClassesListBox { get; set; } = null!;
    [Export] public VBoxContainer GroupsListBox { get; set; } = null!;

    private LocalizationCatalog? _loc;

    public void Initialize(LocalizationCatalog loc) => _loc = loc;

    public override void _Ready()
    {
        SummaryLabel ??= GetNode<Label>("SummaryLabel");
        ClassesListBox ??= GetNode<VBoxContainer>("ClassesListBox");
        GroupsListBox ??= GetNode<VBoxContainer>("GroupsListBox");
    }

    public void Render(RenderSnapshot snap, string lang)
    {
        if (_loc is null || ClassesListBox is null)
            return;

        SummaryLabel.Text = $"{_loc.Get(lang, "hud.world_pop")}: {snap.PlayerPopulation:N0} ({snap.ActivePopCount} world groups)";
        UiThemeFactory.ClearChildren(ClassesListBox);
        UiThemeFactory.ClearChildren(GroupsListBox);

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
            row.AddChild(new Label { Text = _loc.Get(lang, $"class.{cls.ToString().ToLowerInvariant()}"), SizeFlagsHorizontal = SizeFlags.ExpandFill });
            row.AddChild(new Label { Text = $"{count:N0} ({share:F1}%)", CustomMinimumSize = new Vector2(150, 0) });
            ClassesListBox.AddChild(row);
        }

        foreach (var pop in snap.PlayerPops.OrderByDescending(p => p.Population).Take(25))
        {
            var row = UiThemeFactory.CreatePill();
            var h = new HBoxContainer();
            h.AddChild(new Label
            {
                Text = $"{pop.LocationName} · {pop.SocialClass} [{pop.AncestryDisplay}]",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            });
            h.AddChild(new Label { Text = $"{pop.Population:N0} (Lit {pop.Literacy * 100:F0}%)" });
            row.AddChild(h);
            GroupsListBox.AddChild(row);
        }
    }
}
