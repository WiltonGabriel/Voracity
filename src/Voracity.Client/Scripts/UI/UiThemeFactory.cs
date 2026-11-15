using Godot;

namespace Voracity.Client.UI;

/// <summary>
/// Shared UI styling helpers for Voracity's CK3-inspired parchment, brushed steel, and aether aesthetic.
/// </summary>
public static class UiThemeFactory
{
    public static readonly Color PanelBg = new(0.10f, 0.09f, 0.11f, 0.94f);
    public static readonly Color CardBg = new(0.14f, 0.13f, 0.16f, 0.95f);
    public static readonly Color SteelBorder = new(0.42f, 0.37f, 0.28f, 0.95f);
    public static readonly Color GoldAccent = new(0.92f, 0.79f, 0.45f, 1.0f);
    public static readonly Color CyanAether = new(0.42f, 0.78f, 0.88f, 1.0f);
    public static readonly Color CrimsonAlert = new(0.86f, 0.34f, 0.32f, 1.0f);
    public static readonly Color EmeraldPositive = new(0.42f, 0.82f, 0.56f, 1.0f);

    public static PanelContainer CreatePanel(Color? bg = null, Color? border = null, int margin = 12)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", CreateBox(bg ?? PanelBg, border ?? SteelBorder, margin));
        return panel;
    }

    public static StyleBoxFlat CreateBox(Color bg, Color border, int margin = 10) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthTop = 2,
        BorderWidthBottom = 2,
        BorderWidthLeft = 2,
        BorderWidthRight = 2,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        ContentMarginLeft = margin,
        ContentMarginRight = margin,
        ContentMarginTop = margin - 2,
        ContentMarginBottom = margin - 2
    };

    public static Label CreateHeader(string text, int fontSize = 16, Color? color = null)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", color ?? GoldAccent);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    public static void ClearChildren(Node container)
    {
        foreach (var child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }
    }
}
