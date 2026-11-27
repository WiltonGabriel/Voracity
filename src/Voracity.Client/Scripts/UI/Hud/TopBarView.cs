using Godot;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// EU5-inspired Top HUD Bar backed by Scenes/UI/TopBar.tscn with [Export] node bindings.
/// </summary>
public sealed partial class TopBarView : PanelContainer
{
    [Export] public Button CrestButton { get; set; } = null!;
    [Export] public Label NationLabel { get; set; } = null!;
    [Export] public Label DynastyLabel { get; set; } = null!;
    [Export] public Button TreasuryButton { get; set; } = null!;
    [Export] public Button TaxBaseButton { get; set; } = null!;
    [Export] public Label ControlValLabel { get; set; } = null!;
    [Export] public Label FoodValLabel { get; set; } = null!;
    [Export] public Label GovPowerLabel { get; set; } = null!;
    [Export] public Label PopValLabel { get; set; } = null!;
    [Export] public Button PauseButton { get; set; } = null!;
    [Export] public Label DateLabel { get; set; } = null!;
    [Export] public Button Speed1Button { get; set; } = null!;
    [Export] public Button Speed2Button { get; set; } = null!;
    [Export] public Button Speed3Button { get; set; } = null!;
    [Export] public Button Speed4Button { get; set; } = null!;
    [Export] public Button Speed5Button { get; set; } = null!;
    [Export] public Button DebugButton { get; set; } = null!;
    [Export] public Button MenuButton { get; set; } = null!;

    private readonly Button[] _speedBtns = new Button[5];
    private LocalizationCatalog? _loc;
    private string _lang = "pt-BR";

    public event Action<GameCommand>? CommandIssued;
    public event Action<string, Vector2>? ConceptClicked;
    public event Action? DebugToggleRequested;
    public event Action? PauseMenuRequested;
    public event Action? RealmTabRequested;

    public void Initialize(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        SetLanguage(lang);
    }

    public void SetLanguage(string lang)
    {
        _lang = lang;
        if (_loc is null)
            return;
        if (MenuButton is not null)
            MenuButton.Text = _loc.Get(_lang, "hud.menu_btn");
        if (DebugButton is not null)
            DebugButton.Text = _loc.Get(_lang, "debug.badge");
    }

    public void SetDebugButtonVisible(bool visible)
    {
        if (DebugButton is not null)
            DebugButton.Visible = visible;
    }

    public override void _Ready()
    {
        CrestButton ??= GetNode<Button>("Row/CrestBtn");
        NationLabel ??= GetNode<Label>("Row/CrestBtn/CrestMargin/CrestVBox/NationLabel");
        DynastyLabel ??= GetNode<Label>("Row/CrestBtn/CrestMargin/CrestVBox/DynastyLabel");
        TreasuryButton ??= GetNode<Button>("Row/TreasuryBtn");
        TaxBaseButton ??= GetNode<Button>("Row/TaxBaseBtn");
        ControlValLabel ??= GetNode<Label>("Row/CtrlPill/ControlValLabel");
        FoodValLabel ??= GetNode<Label>("Row/FoodPill/FoodValLabel");
        GovPowerLabel ??= GetNode<Label>("Row/GovPill/GovPowerLabel");
        PopValLabel ??= GetNode<Label>("Row/PopPill/PopValLabel");
        PauseButton ??= GetNode<Button>("Row/ClockBox/PauseBtn");
        DateLabel ??= GetNode<Label>("Row/ClockBox/DateLabel");
        Speed1Button ??= GetNode<Button>("Row/ClockBox/Speed1Btn");
        Speed2Button ??= GetNode<Button>("Row/ClockBox/Speed2Btn");
        Speed3Button ??= GetNode<Button>("Row/ClockBox/Speed3Btn");
        Speed4Button ??= GetNode<Button>("Row/ClockBox/Speed4Btn");
        Speed5Button ??= GetNode<Button>("Row/ClockBox/Speed5Btn");
        DebugButton ??= GetNode<Button>("Row/ClockBox/DebugBtn");
        MenuButton ??= GetNode<Button>("Row/ClockBox/MenuBtn");

        _speedBtns[0] = Speed1Button;
        _speedBtns[1] = Speed2Button;
        _speedBtns[2] = Speed3Button;
        _speedBtns[3] = Speed4Button;
        _speedBtns[4] = Speed5Button;

        CrestButton.Pressed += () => RealmTabRequested?.Invoke();
        TreasuryButton.Pressed += () => ConceptClicked?.Invoke("treasury", TreasuryButton.GlobalPosition);
        TaxBaseButton.Pressed += () => ConceptClicked?.Invoke("tax_base", TaxBaseButton.GlobalPosition);
        PauseButton.Pressed += () =>
        {
            var currentlyPaused = PauseButton.Text == ">";
            CommandIssued?.Invoke(new SetPausedCommand(!currentlyPaused));
        };

        for (var i = 0; i < 5; i++)
        {
            var spd = i + 1;
            _speedBtns[i].Pressed += () => CommandIssued?.Invoke(new SetSpeedCommand(spd));
        }

        DebugButton.Pressed += () => DebugToggleRequested?.Invoke();
        MenuButton.Pressed += () => PauseMenuRequested?.Invoke();

        if (_loc is not null)
            SetLanguage(_lang);
    }

    public void UpdateFromSnapshot(RenderSnapshot snap)
    {
        if (_loc is null)
            return;

        NationLabel.Text = $"{snap.Realm.NationName} [{snap.Realm.NationId}]";
        var traits = snap.Realm.RulerTraits.Count > 0 ? $" · {string.Join(", ", snap.Realm.RulerTraits)}" : string.Empty;
        DynastyLabel.Text = $"{snap.Realm.DynastyName}{traits}";

        var sign = snap.MonthlyBalance.CopperUnits >= 0 ? "+" : "";
        var balGold = snap.MonthlyBalance.ToGold();
        TreasuryButton.Text = $"{snap.Treasury.ToGold():N0} {_loc.Get(_lang, "currency.gold")} ({sign}{balGold:F1}{_loc.Get(_lang, "hud.monthly")})";

        TaxBaseButton.Text = $"{_loc.Get(_lang, "hud.tax_base")}: {snap.TotalEffectiveTaxBase:F0}/{snap.TotalPotentialTaxBase:F0}";
        ControlValLabel.Text = $"{_loc.Get(_lang, "hud.control")}: {snap.AverageControl:F1}%";

        var foodSign = snap.TotalFoodBalance >= 0 ? "+" : "";
        FoodValLabel.Text = snap.StarvingProvincesCount > 0
            ? $"{_loc.Get(_lang, "hud.food")}: {foodSign}{snap.TotalFoodBalance:F0} (!{snap.StarvingProvincesCount})"
            : $"{_loc.Get(_lang, "hud.food")}: {foodSign}{snap.TotalFoodBalance:F0}";
        FoodValLabel.AddThemeColorOverride("font_color", snap.StarvingProvincesCount > 0 ? UiThemeFactory.CrimsonAlert : UiThemeFactory.EmeraldPositive);

        GovPowerLabel.Text = $"{_loc.Get(_lang, "hud.gov_power")}: {snap.Realm.GovernmentPower:F0}";
        PopValLabel.Text = $"{_loc.Get(_lang, "hud.world_pop")}: {snap.PlayerPopulation:N0}";

        UpdateClockOnly(snap.Date, snap.IsPaused, snap.Speed);
    }

    public void UpdateClockOnly(GameDate date, bool paused, int speed)
    {
        if (_loc is null || PauseButton is null)
            return;

        PauseButton.Text = paused ? ">" : "||";
        DateLabel.Text = paused ? $"{date.ToDisplayString()} [{_loc.Get(_lang, "hud.paused")}]" : date.ToDisplayString();
        for (var i = 0; i < 5; i++)
            UiThemeFactory.ApplyButtonHighlight(_speedBtns[i], speed == i + 1);
    }
}
