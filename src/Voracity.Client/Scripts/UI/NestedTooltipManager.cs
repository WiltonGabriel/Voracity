using Godot;

namespace Voracity.Client.UI;

/// <summary>
/// CK3-inspired recursive Nested Tooltip stack with Action Lock (middle-click / Esc to dismiss).
/// Supports live switching between pt-BR and en-US.
/// </summary>
public partial class NestedTooltipManager : Control
{
    private readonly List<PanelContainer> _stack = new();
    private string _languageCode = "pt-BR";
    private bool _locked;

    public void SetLanguage(string languageCode) => _languageCode = languageCode;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
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

        var panel = UiThemeFactory.CreatePanel(
            new Color(0.11f, 0.10f, 0.12f, 0.97f),
            UiThemeFactory.CyanAether,
            12);
        panel.Position = ClampToViewport(screenPos + new Vector2(18f, 18f));
        panel.CustomMinimumSize = new Vector2(340f, 0f);
        panel.MouseFilter = MouseFilterEnum.Stop;

        var vbox = new VBoxContainer();
        var hint = _languageCode.StartsWith("pt", StringComparison.OrdinalIgnoreCase)
            ? "(Botão do Meio: Travar | Esc: Fechar)"
            : "(Middle-Click: Lock | Esc: Close)";
        var titleLabel = UiThemeFactory.CreateHeader($"{title}  {hint}", 13);
        vbox.AddChild(titleLabel);

        var rich = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            Text = body,
            CustomMinimumSize = new Vector2(315f, 40f)
        };
        rich.MetaClicked += meta =>
        {
            var id = meta.AsString().Replace("concept:", string.Empty);
            ShowConcept(id, level + 1, panel.Position + new Vector2(panel.Size.X + 8f, 0f));
        };
        vbox.AddChild(rich);
        panel.AddChild(vbox);

        AddChild(panel);
        _stack.Add(panel);
    }

    public void ClearAll()
    {
        _locked = false;
        foreach (var panel in _stack)
            panel.QueueFree();
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
            Mathf.Clamp(pos.X, 12f, MathF.Max(12f, vp.X - 360f)),
            Mathf.Clamp(pos.Y, 12f, MathF.Max(12f, vp.Y - 190f)));
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
