using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// EU5-inspired Top HUD Bar: Realm crest + Dynasty/Traits pill, Treasury &amp; Monthly Balance,
/// Tax Base / Avg Control, Food Balance &amp; Starving badge, World Population, and Speed 1x–5x + Pause.
/// </summary>
public sealed partial class TopBarView : PanelContainer
{
    private readonly LocalizationCatalog _loc;
    private string _lang = "pt-BR";

    private Label _nationLabel = null!;
    private Label _dynastyLabel = null!;
    private Button _treasuryBtn = null!;
    private Button _taxBaseBtn = null!;
    private Label _controlValLabel = null!;
    private Label _foodValLabel = null!;
    private Label _popValLabel = null!;
    private Label _govPowerLabel = null!;

    private Button _pauseBtn = null!;
    private Label _dateLabel = null!;
    private readonly Button[] _speedBtns = new Button[5];
    private Button _debugBtn = null!;
    private Button _menuBtn = null!;

    public event Action<GameCommand>? CommandIssued;
    public event Action<string, Vector2>? ConceptClicked;
    public event Action? DebugToggleRequested;
    public event Action? PauseMenuRequested;
    public event Action? RealmTabRequested;

    public TopBarView(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        _lang = lang;
    }

    public void SetLanguage(string lang)
    {
        _lang = lang;
        if (_menuBtn is not null)
            _menuBtn.Text = _loc.Get(_lang, "hud.menu_btn");
        if (_debugBtn is not null)
            _debugBtn.Text = _loc.Get(_lang, "debug.badge");
    }

    public void SetDebugButtonVisible(bool visible)
    {
        if (_debugBtn is not null)
            _debugBtn.Visible = visible;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopWide);
        CustomMinimumSize = new Vector2(0, 60);
        OffsetLeft = 8;
        OffsetRight = -8;
        OffsetTop = 6;
        OffsetBottom = 66;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiThemeFactory.CreateBox(UiThemeFactory.HeaderBg, UiThemeFactory.SteelBorder, 8));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        AddChild(row);

        var crestBtn = UiThemeFactory.CreateStyledButton(string.Empty, 230, 42, UiThemeFactory.GoldAccent);
        crestBtn.Pressed += () => RealmTabRequested?.Invoke();
        var crestVBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        crestVBox.SetAnchorsPreset(LayoutPreset.FullRect);
        _nationLabel = new Label { Text = "Voracity", MouseFilter = MouseFilterEnum.Ignore };
        _nationLabel.AddThemeFontSizeOverride("font_size", 14);
        _nationLabel.AddThemeColorOverride("font_color", UiThemeFactory.GoldAccent);
        _dynastyLabel = new Label { Text = string.Empty, MouseFilter = MouseFilterEnum.Ignore };
        _dynastyLabel.AddThemeFontSizeOverride("font_size", 10);
        _dynastyLabel.AddThemeColorOverride("font_color", UiThemeFactory.MutedText);
        crestVBox.AddChild(_nationLabel);
        crestVBox.AddChild(_dynastyLabel);
        crestBtn.AddChild(crestVBox);
        row.AddChild(crestBtn);

        _treasuryBtn = UiThemeFactory.CreateStyledButton("0 Ouro", 165, 40);
        _treasuryBtn.Pressed += () => ConceptClicked?.Invoke("treasury", _treasuryBtn.GlobalPosition);
        row.AddChild(_treasuryBtn);

        _taxBaseBtn = UiThemeFactory.CreateStyledButton("Tax Base: 0", 130, 40);
        _taxBaseBtn.Pressed += () => ConceptClicked?.Invoke("tax_base", _taxBaseBtn.GlobalPosition);
        row.AddChild(_taxBaseBtn);

        var ctrlPill = CreateMetricPill("CTRL", UiThemeFactory.CyanAether, out _controlValLabel);
        row.AddChild(ctrlPill);

        var foodPill = CreateMetricPill("FOOD", UiThemeFactory.EmeraldPositive, out _foodValLabel);
        row.AddChild(foodPill);

        var govPill = CreateMetricPill("GOV", UiThemeFactory.GoldAccent, out _govPowerLabel);
        row.AddChild(govPill);

        var popPill = CreateMetricPill("POP", UiThemeFactory.BronzeAccent, out _popValLabel);
        row.AddChild(popPill);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var clockBox = new HBoxContainer();
        clockBox.AddThemeConstantOverride("separation", 5);
        row.AddChild(clockBox);

        _pauseBtn = UiThemeFactory.CreateStyledButton("||", 40, 36);
        _pauseBtn.Pressed += () =>
        {
            var currentlyPaused = _pauseBtn.Text == ">";
            CommandIssued?.Invoke(new SetPausedCommand(!currentlyPaused));
        };
        clockBox.AddChild(_pauseBtn);

        _dateLabel = new Label
        {
            Text = "01/01/1380 D.Q.",
            CustomMinimumSize = new Vector2(180, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _dateLabel.AddThemeColorOverride("font_color", UiThemeFactory.GoldAccent);
        _dateLabel.AddThemeFontSizeOverride("font_size", 14);
        clockBox.AddChild(_dateLabel);

        for (var i = 0; i < 5; i++)
        {
            var spd = i + 1;
            var btn = UiThemeFactory.CreateStyledButton($"{spd}x", 34, 34);
            btn.Pressed += () => CommandIssued?.Invoke(new SetSpeedCommand(spd));
            _speedBtns[i] = btn;
            clockBox.AddChild(btn);
        }

        _debugBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "debug.badge"), 88, 36, UiThemeFactory.CyanAether);
        _debugBtn.Visible = false;
        _debugBtn.Pressed += () => DebugToggleRequested?.Invoke();
        clockBox.AddChild(_debugBtn);

        _menuBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "hud.menu_btn"), 92, 36);
        _menuBtn.Pressed += () => PauseMenuRequested?.Invoke();
        clockBox.AddChild(_menuBtn);
    }

    public void UpdateFromSnapshot(RenderSnapshot snap)
    {
        _nationLabel.Text = $"{snap.Realm.NationName} [{snap.Realm.NationId}]";
        var traits = snap.Realm.RulerTraits.Count > 0 ? $" · {string.Join(", ", snap.Realm.RulerTraits)}" : string.Empty;
        _dynastyLabel.Text = $"{snap.Realm.DynastyName}{traits}";

        var sign = snap.MonthlyBalance.CopperUnits >= 0 ? "+" : "";
        var balGold = snap.MonthlyBalance.ToGold();
        _treasuryBtn.Text = $"{snap.Treasury.ToGold():N0} {_loc.Get(_lang, "currency.gold")} ({sign}{balGold:F1}{_loc.Get(_lang, "hud.monthly")})";

        _taxBaseBtn.Text = $"{_loc.Get(_lang, "hud.tax_base")}: {snap.TotalEffectiveTaxBase:F0}/{snap.TotalPotentialTaxBase:F0}";
        _controlValLabel.Text = $"{_loc.Get(_lang, "hud.control")}: {snap.AverageControl:F1}%";

        var foodSign = snap.TotalFoodBalance >= 0 ? "+" : "";
        _foodValLabel.Text = snap.StarvingProvincesCount > 0
            ? $"{_loc.Get(_lang, "hud.food")}: {foodSign}{snap.TotalFoodBalance:F0} (!{snap.StarvingProvincesCount})"
            : $"{_loc.Get(_lang, "hud.food")}: {foodSign}{snap.TotalFoodBalance:F0}";
        _foodValLabel.AddThemeColorOverride("font_color", snap.StarvingProvincesCount > 0 ? UiThemeFactory.CrimsonAlert : UiThemeFactory.EmeraldPositive);

        _govPowerLabel.Text = $"{_loc.Get(_lang, "hud.gov_power")}: {snap.Realm.GovernmentPower:F0}";
        _popValLabel.Text = $"{_loc.Get(_lang, "hud.world_pop")}: {snap.PlayerPopulation:N0}";

        UpdateClockOnly(snap.Date, snap.IsPaused, snap.Speed);
    }

    public void UpdateClockOnly(GameDate date, bool paused, int speed)
    {
        _pauseBtn.Text = paused ? ">" : "||";
        _dateLabel.Text = paused ? $"{date.ToDisplayString()} [{_loc.Get(_lang, "hud.paused")}]" : date.ToDisplayString();
        for (var i = 0; i < 5; i++)
            UiThemeFactory.ApplyButtonHighlight(_speedBtns[i], speed == i + 1);
    }

    private static PanelContainer CreateMetricPill(string tag, Color color, out Label valueLabel)
    {
        var pill = UiThemeFactory.CreatePill(UiThemeFactory.PillBg, UiThemeFactory.SteelBorder, 8, 4);
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        pill.AddChild(box);
        valueLabel = new Label { Text = tag };
        valueLabel.AddThemeFontSizeOverride("font_size", 12);
        valueLabel.AddThemeColorOverride("font_color", color);
        box.AddChild(valueLabel);
        return pill;
    }
}
