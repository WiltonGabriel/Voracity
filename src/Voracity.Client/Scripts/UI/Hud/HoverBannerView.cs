using Godot;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Compact bottom-right hover card displaying live EU5 metadata for the hovered Location.
/// </summary>
public sealed partial class HoverBannerView : PanelContainer
{
    private Label _label = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.BottomRight);
        OffsetLeft = -410;
        OffsetRight = -10;
        OffsetTop = -152;
        OffsetBottom = -64;
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeStyleboxOverride("panel", UiThemeFactory.CreateBox(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 8));

        _label = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _label.AddThemeFontSizeOverride("font_size", 12);
        AddChild(_label);
    }

    public void SetHoveredLocation(ProvinceSnapshot? loc)
    {
        if (loc is null)
        {
            Visible = false;
            return;
        }

        Visible = true;
        var name = loc.DynamicDisplayName ?? loc.Name;
        if (loc.Kind == LocationKind.SeaZone)
        {
            _label.Text = $"{name} [SeaZone]\nMaritime Presence: {loc.SeaZoneMaritimePresence * 100.0:F0}%";
            return;
        }

        var netFood = loc.LocalFoodProduced - loc.LocalFoodConsumed;
        _label.Text =
            $"{name} [{loc.OwnerNationId}] · {loc.Rank} · {loc.Topography}/{loc.Vegetation}\n" +
            $"RGO: {loc.ResourcePotential ?? "grain"} Lv.{loc.RgoLevel}  |  Ctrl: {loc.Control:F0}%  |  Access: {loc.MarketAccess * 100.0:F0}%\n" +
            $"Pop: {loc.Population:N0}  |  Tax Base: {loc.EffectiveTaxBase:F1}  |  Food: {(netFood >= 0 ? "+" : "")}{netFood:F1}";
    }
}
