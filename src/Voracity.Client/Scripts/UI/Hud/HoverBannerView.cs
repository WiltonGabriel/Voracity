using Godot;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Compact bottom-right hover card backed by Scenes/UI/HoverBanner.tscn.
/// </summary>
public sealed partial class HoverBannerView : PanelContainer
{
    [Export] public Label SummaryLabel { get; set; } = null!;

    public override void _Ready()
    {
        SummaryLabel ??= GetNode<Label>("SummaryLabel");
    }

    public void SetHoveredLocation(ProvinceSnapshot? loc)
    {
        if (SummaryLabel is null)
            return;

        if (loc is null)
        {
            Visible = false;
            return;
        }

        Visible = true;
        var name = loc.DynamicDisplayName ?? loc.Name;
        if (loc.Kind == LocationKind.SeaZone)
        {
            SummaryLabel.Text = $"{name} [SeaZone]\nMaritime Presence: {loc.SeaZoneMaritimePresence * 100.0:F0}%";
            return;
        }

        var netFood = loc.LocalFoodProduced - loc.LocalFoodConsumed;
        SummaryLabel.Text =
            $"{name} [{loc.OwnerNationId}] · {loc.Rank} · {loc.Topography}/{loc.Vegetation}\n" +
            $"RGO: {loc.ResourcePotential ?? "grain"} Lv.{loc.RgoLevel}  |  Ctrl: {loc.Control:F0}%  |  Access: {loc.MarketAccess * 100.0:F0}%\n" +
            $"Pop: {loc.Population:N0}  |  Tax Base: {loc.EffectiveTaxBase:F1}  |  Food: {(netFood >= 0 ? "+" : "")}{netFood:F1}";
    }
}
