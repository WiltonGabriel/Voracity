using Godot;

namespace Voracity.Client.UI;

/// <summary>
/// Shared UI palette and lightweight factory helpers backing VoracityTheme.tres.
/// Dynamic list items inherit styling from VoracityTheme.tres variations so edits in the Godot Theme Inspector apply everywhere.
/// </summary>
public static class UiThemeFactory
{
    public static readonly Color PanelBg = new(0.10f, 0.09f, 0.11f, 0.94f);
    public static readonly Color HeaderBg = new(0.16f, 0.14f, 0.17f, 0.96f);
    public static readonly Color CardBg = new(0.14f, 0.13f, 0.16f, 0.95f);
    public static readonly Color PillBg = new(0.13f, 0.12f, 0.15f, 0.94f);
    public static readonly Color ActivePillBg = new(0.24f, 0.20f, 0.14f, 0.98f);
    public static readonly Color SteelBorder = new(0.42f, 0.37f, 0.28f, 0.95f);
    public static readonly Color BronzeAccent = new(0.68f, 0.54f, 0.32f, 1.0f);
    public static readonly Color GoldAccent = new(0.92f, 0.79f, 0.45f, 1.0f);
    public static readonly Color CyanAether = new(0.42f, 0.78f, 0.88f, 1.0f);
    public static readonly Color AmberAlert = new(0.92f, 0.64f, 0.24f, 1.0f);
    public static readonly Color CrimsonAlert = new(0.86f, 0.34f, 0.32f, 1.0f);
    public static readonly Color EmeraldPositive = new(0.42f, 0.82f, 0.56f, 1.0f);
    public static readonly Color MutedText = new(0.73f, 0.70f, 0.66f, 1.0f);

    public static PanelContainer CreatePanel(Color? bg = null, Color? border = null, int margin = 12)
    {
        var panel = new PanelContainer();
        if (bg is null && border is null)
        {
            panel.ThemeTypeVariation = "CardPanel";
            return panel;
        }

        panel.AddThemeStyleboxOverride("panel", CreateBox(bg ?? PanelBg, border ?? SteelBorder, margin));
        return panel;
    }

    public static PanelContainer CreatePill(Color? bg = null, Color? border = null, int hMargin = 9, int vMargin = 4)
    {
        var panel = new PanelContainer();
        if (bg is null && border is null)
        {
            panel.ThemeTypeVariation = "PillPanel";
            return panel;
        }

        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = bg ?? PillBg,
            BorderColor = border ?? SteelBorder,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = hMargin,
            ContentMarginRight = hMargin,
            ContentMarginTop = vMargin,
            ContentMarginBottom = vMargin
        });
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
        ContentMarginTop = Math.Max(2, margin - 2),
        ContentMarginBottom = Math.Max(2, margin - 2)
    };

    public static Button CreateStyledButton(string text = "", int minW = 0, int minH = 30, Color? border = null, bool active = false)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(minW, minH)
        };
        if (border is null)
        {
            btn.ThemeTypeVariation = active ? "AccentButton" : "";
            return btn;
        }

        var bCol = active ? GoldAccent : border.Value;
        var bg = active ? ActivePillBg : CardBg;
        btn.AddThemeStyleboxOverride("normal", CreateButtonBox(bg, bCol));
        btn.AddThemeColorOverride("font_color", active ? GoldAccent : new Color(0.93f, 0.90f, 0.84f));
        return btn;
    }

    public static void ApplyButtonHighlight(Button btn, bool active, Color? defaultBorder = null)
    {
        var curVar = btn.ThemeTypeVariation?.ToString() ?? string.Empty;
        if (defaultBorder is null && (curVar.Length == 0 || curVar == "AccentButton"))
        {
            btn.RemoveThemeStyleboxOverride("normal");
            btn.RemoveThemeColorOverride("font_color");
            btn.ThemeTypeVariation = active ? "AccentButton" : "";
            return;
        }

        var bCol = active ? GoldAccent : (defaultBorder ?? SteelBorder);
        var bg = active ? ActivePillBg : CardBg;
        btn.AddThemeStyleboxOverride("normal", CreateButtonBox(bg, bCol));
        btn.AddThemeColorOverride("font_color", active ? GoldAccent : new Color(0.93f, 0.90f, 0.84f));
    }

    private static StyleBoxFlat CreateButtonBox(Color bg, Color border) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthTop = 1,
        BorderWidthBottom = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusTopLeft = 3,
        CornerRadiusTopRight = 3,
        CornerRadiusBottomLeft = 3,
        CornerRadiusBottomRight = 3,
        ContentMarginLeft = 8,
        ContentMarginRight = 8,
        ContentMarginTop = 4,
        ContentMarginBottom = 4
    };

    public static Color GetTerrainBannerColor(string? terrain) => (terrain ?? string.Empty).ToLowerInvariant() switch
    {
        "farmlands" => new Color(0.36f, 0.48f, 0.22f, 0.92f),
        "plains" => new Color(0.42f, 0.46f, 0.28f, 0.92f),
        "forest" => new Color(0.18f, 0.36f, 0.23f, 0.92f),
        "hills" => new Color(0.44f, 0.36f, 0.24f, 0.92f),
        "mountains" => new Color(0.34f, 0.33f, 0.38f, 0.95f),
        "desert" => new Color(0.62f, 0.45f, 0.23f, 0.92f),
        "coastal" => new Color(0.21f, 0.41f, 0.48f, 0.92f),
        "swamp" => new Color(0.22f, 0.32f, 0.28f, 0.92f),
        "tundra" => new Color(0.44f, 0.50f, 0.55f, 0.92f),
        _ => new Color(0.24f, 0.22f, 0.25f, 0.92f)
    };

    public static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;
        try
        {
            return Color.FromHtml(hex);
        }
        catch
        {
            return fallback;
        }
    }

    public static Label CreateHeader(string text, int fontSize = 16, Color? color = null)
    {
        var label = new Label { Text = text, ThemeTypeVariation = "HeaderLabel" };
        if (color.HasValue)
            label.AddThemeColorOverride("font_color", color.Value);
        if (fontSize != 15)
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
