using Godot;
using Voracity.Core.Data;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Fullscreen Pause Overlay backed by Scenes/UI/PauseMenu.tscn.
/// </summary>
public sealed partial class PauseMenuOverlay : ColorRect
{
    [Export] public Label TitleLabel { get; set; } = null!;
    [Export] public Button ResumeButton { get; set; } = null!;
    [Export] public Button SaveButton { get; set; } = null!;
    [Export] public Button SettingsButton { get; set; } = null!;
    [Export] public Button MainMenuButton { get; set; } = null!;
    [Export] public Button QuitButton { get; set; } = null!;
    [Export] public Label FeedbackLabel { get; set; } = null!;

    private LocalizationCatalog? _loc;
    private string _lang = "pt-BR";

    public event Action? ResumeRequested;
    public event Action? QuicksaveRequested;
    public event Action? SettingsRequested;
    public event Action? MainMenuRequested;

    public void Initialize(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        SetLanguage(lang);
    }

    public void SetLanguage(string lang)
    {
        _lang = lang;
        if (_loc is null || TitleLabel is null)
            return;

        TitleLabel.Text = _loc.Get(_lang, "pause.title");
        ResumeButton.Text = _loc.Get(_lang, "pause.resume");
        SaveButton.Text = _loc.Get(_lang, "pause.quicksave");
        SettingsButton.Text = _loc.Get(_lang, "pause.settings");
        MainMenuButton.Text = _loc.Get(_lang, "pause.main_menu");
        QuitButton.Text = _loc.Get(_lang, "pause.quit");
    }

    public void NotifySaveSucceeded()
    {
        if (FeedbackLabel is not null && _loc is not null)
            FeedbackLabel.Text = _loc.Get(_lang, "pause.saved_ok");
    }

    public void Open()
    {
        if (FeedbackLabel is not null)
            FeedbackLabel.Text = string.Empty;
        Visible = true;
    }

    public override void _Ready()
    {
        TitleLabel ??= GetNode<Label>("Center/Panel/VBox/TitleLabel");
        ResumeButton ??= GetNode<Button>("Center/Panel/VBox/ResumeBtn");
        SaveButton ??= GetNode<Button>("Center/Panel/VBox/SaveBtn");
        SettingsButton ??= GetNode<Button>("Center/Panel/VBox/SettingsBtn");
        MainMenuButton ??= GetNode<Button>("Center/Panel/VBox/MenuBtn");
        QuitButton ??= GetNode<Button>("Center/Panel/VBox/QuitBtn");
        FeedbackLabel ??= GetNode<Label>("Center/Panel/VBox/FeedbackLabel");

        ResumeButton.Pressed += () => { Visible = false; ResumeRequested?.Invoke(); };
        SaveButton.Pressed += () => QuicksaveRequested?.Invoke();
        SettingsButton.Pressed += () => SettingsRequested?.Invoke();
        MainMenuButton.Pressed += () => { Visible = false; MainMenuRequested?.Invoke(); };
        QuitButton.Pressed += () => GetTree().Quit();

        if (_loc is not null)
            SetLanguage(_lang);
    }
}
