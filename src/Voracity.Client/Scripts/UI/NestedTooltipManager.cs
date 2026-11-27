using Godot;

namespace Voracity.Client.UI;

/// <summary>
/// CK3/EU5-inspired recursive Nested Tooltip stack with Action Lock (middle-click / Esc to dismiss),
/// instantiating modular NestedTooltipCard.tscn panels and animating them with UiMotionAnimator.
/// </summary>
public partial class NestedTooltipManager : Control
{
    [Export] public PackedScene? TooltipCardScene { get; set; }

    [ExportGroup("Layout & Motion")]
    [Export] public Vector2 CursorOffset { get; set; } = new(18f, 18f);
    [Export] public Vector2 CascadeOffset { get; set; } = new(10f, 0f);
    [Export] public float ViewportMargin { get; set; } = 12f;
    [Export(PropertyHint.Range, "0.0,0.4,0.01")] public float FadeDurationSeconds { get; set; } = 0.10f;

    private readonly List<NestedTooltipCardView> _stack = new();
    private string _languageCode = "pt-BR";
    private bool _locked;

    public void SetLanguage(string languageCode) => _languageCode = languageCode;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        TooltipCardScene ??= GD.Load<PackedScene>("res://Scenes/UI/NestedTooltipCard.tscn");
    }

    public override void _UnhandledInput(InputEvent evt)
    {
        if (_stack.Count == 0)
            return;

        if (evt is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            ClearAll();
        }
        else if (evt is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Middle })
        {
            _locked = !_locked;
            var hint = BuildLockHint(_languageCode, _locked);
            foreach (var card in _stack)
            {
                card.SetLocked(_locked);
                if (card.LockHintLabel is not null)
                    card.LockHintLabel.Text = hint;
            }
        }
        else if (_locked && evt is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            var mouse = GetGlobalMousePosition();
            var insideAny = _stack.Any(p => p.GetGlobalRect().HasPoint(mouse));
            if (!insideAny)
                ClearAll();
        }
    }

    public void ShowConcept(string conceptId, int level, Vector2 screenPos)
    {
        var (title, body) = ResolveConcept(conceptId, _languageCode);
        if (title.Length == 0)
            return;

        TrimToLevel(level);

        TooltipCardScene ??= GD.Load<PackedScene>("res://Scenes/UI/NestedTooltipCard.tscn");
        var card = TooltipCardScene is not null
            ? TooltipCardScene.Instantiate<NestedTooltipCardView>()
            : CreateFallbackCard();

        card.Position = ClampToViewport(screenPos + CursorOffset);
        AddChild(card);

        var formattedBody = StyleConceptLinks(body);
        card.Configure(title, formattedBody, BuildLockHint(_languageCode, _locked), level, _locked);
        card.ConceptMetaClicked += (id, nextLevel, sourceCard) =>
        {
            var nextPos = sourceCard.Position + new Vector2(sourceCard.Size.X + CascadeOffset.X, CascadeOffset.Y);
            ShowConcept(id, nextLevel, nextPos);
        };

        _stack.Add(card);
        UiMotionAnimator.AnimatePopupIn(card, FadeDurationSeconds);
    }

    public void ClearAll()
    {
        _locked = false;
        foreach (var card in _stack)
            card.QueueFree();
        _stack.Clear();
    }

    private void TrimToLevel(int targetLevel)
    {
        while (_stack.Count > targetLevel)
        {
            var last = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            last.QueueFree();
        }
    }

    private Vector2 ClampToViewport(Vector2 pos)
    {
        var vp = GetViewportRect().Size;
        return new Vector2(
            Mathf.Clamp(pos.X, ViewportMargin, MathF.Max(ViewportMargin, vp.X - 364f)),
            Mathf.Clamp(pos.Y, ViewportMargin, MathF.Max(ViewportMargin, vp.Y - 196f)));
    }

    private static string BuildLockHint(string lang, bool locked)
    {
        var pt = lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        return (pt, locked) switch
        {
            (true, true) => "Travado · Esc",
            (true, false) => "Meio: Travar",
            (false, true) => "Locked · Esc",
            (false, false) => "MMB: Lock"
        };
    }

    private static string StyleConceptLinks(string bodyBbcode) =>
        bodyBbcode
            .Replace("[url=", "[color=#72B4F5][url=", StringComparison.Ordinal)
            .Replace("[/url]", "[/url][/color]", StringComparison.Ordinal);

    private static NestedTooltipCardView CreateFallbackCard()
    {
        var card = new NestedTooltipCardView
        {
            ThemeTypeVariation = "TooltipPanel",
            CustomMinimumSize = new Vector2(348f, 0f),
            MouseFilter = MouseFilterEnum.Stop
        };
        var vbox = new VBoxContainer();
        var header = new HBoxContainer();
        card.TitleLabel = UiThemeFactory.CreateHeader("Concept", 14);
        card.TitleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        card.LockIcon = new TextureRect { CustomMinimumSize = new Vector2(14f, 14f) };
        card.LockHintLabel = new Label { ThemeTypeVariation = "MutedLabel" };
        header.AddChild(card.TitleLabel);
        header.AddChild(card.LockIcon);
        header.AddChild(card.LockHintLabel);
        vbox.AddChild(header);
        vbox.AddChild(new HSeparator());
        card.BodyRichText = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(316f, 28f)
        };
        vbox.AddChild(card.BodyRichText);
        card.AddChild(vbox);
        return card;
    }

    private static (string Title, string Body) ResolveConcept(string id, string lang)
    {
        var pt = lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        return id switch
        {
            "treasury" => pt
                ? ("Tesouro & Padrão 100:1", "Unidade interna em Ouros. [url=concept:copper_ratio]1 Ouro = 100 Pratas = 10.000 Cobres[/url]. Sustenta exércitos, burocracia e [url=concept:farms]Construções & PMs[/url].")
                : ("Treasury & 100:1 Standard", "Internal accounting in Gold. [url=concept:copper_ratio]1 Gold = 100 Silver = 10,000 Copper[/url]. Funds armies, bureaucracy, and [url=concept:farms]Buildings & PMs[/url]."),

            "copper_ratio" => pt
                ? ("Conversão Monetária", "1 Platina = 100 Ouros | 1 Ouro = 100 Pratas | 1 Prata = 100 Cobres. Cálculos internos usam aritmética inteira determinística.")
                : ("Monetary Conversion", "1 Platinum = 100 Gold | 1 Gold = 100 Silver | 1 Silver = 100 Copper. Internal math uses deterministic integer arithmetic."),

            "dual_ancestry" => pt
                ? ("Dupla Ancestralidade (Ancestralidade ≠ Raça)", "Cada Pop ou governante suporta até 2 ancestralidades (ex.: Humana + Orc = Meio-Orc), herdando 50%/50% dos modificadores demográficos e alimentares.")
                : ("Dual Ancestry (Ancestry != Race)", "Each Pop or ruler holds up to 2 ancestry slots (e.g., Human + Orc = Half-Orc), blending biological modifiers 50%/50%."),

            "tax_base" => pt
                ? ("Tax Base Efetiva & Controle", "Tax Base Potencial (RGO + Construções) multiplicada pelo [url=concept:control_proximity]Controle Local[/url]. Distribuída por pesos aos Estamentos e taxada pela Coroa.")
                : ("Effective Tax Base & Control", "Potential Tax Base (RGO + Buildings) scaled by [url=concept:control_proximity]Local Control[/url], then distributed across Estate Tax Weights."),

            "control_proximity" => pt
                ? ("Proximidade & Controle (EU5)", "Proximidade irradia da Capital via grafo Dijkstra (estradas, rios e portos). O Controle Máximo escala a Tax Base Efetiva de cada Location.")
                : ("Proximity & Control (EU5)", "Proximity radiates from the Capital along Dijkstra paths (roads, rivers, and harbors). Max Control scales each Location's Effective Tax Base."),

            "farms" => pt
                ? ("Construções & Métodos de Produção", "Cada nível gera ordens de compra e venda no [url=concept:market_pricing]Mercado Regional[/url], priorizando Locations com maior Market Access.")
                : ("Buildings & Production Methods", "Each building level emits buy and sell orders into the [url=concept:market_pricing]Regional Market[/url], prioritized by Market Access."),

            "market_pricing" => pt
                ? ("Preço Flutuante (-75% a +75%)", "P = P₀ × (1 + 0,75 × (D − S) / max(D, S)). Escassez puxa preços até +75%; excesso derruba até -75%.")
                : ("Floating Market Prices (-75% to +75%)", "P = P₀ × (1 + 0.75 × (D − S) / max(D, S)). Shortages push prices up to +75%; surpluses drop down to -75%."),

            _ => (string.Empty, string.Empty)
        };
    }
}
