using Godot;
using Voracity.Core.Data;

namespace Voracity.Client.UI.Hud;

/// <summary>
/// Fullscreen Pause Overlay with Resume, Quicksave, Settings, Main Menu, and Quit.
/// </summary>
public sealed partial class PauseMenuOverlay : ColorRect
{
    private readonly LocalizationCatalog _loc;
    private string _lang = "pt-BR";

    private Label _titleLabel = null!;
    private Label _feedbackLabel = null!;
    private Button _resumeBtn = null!;
    private Button _saveBtn = null!;
    private Button _settingsBtn = null!;
    private Button _menuBtn = null!;
    private Button _quitBtn = null!;

    public event Action? ResumeRequested;
    public event Action? QuicksaveRequested;
    public event Action? SettingsRequested;
    public event Action? MainMenuRequested;

    public PauseMenuOverlay(LocalizationCatalog loc, string lang)
    {
        _loc = loc;
        _lang = lang;
    }

    public void SetLanguage(string lang)
    {
        _lang = lang;
        if (_titleLabel is null)
            return;
        _titleLabel.Text = _loc.Get(_lang, "pause.title");
        _resumeBtn.Text = _loc.Get(_lang, "pause.resume");
        _saveBtn.Text = _loc.Get(_lang, "pause.quicksave");
        _settingsBtn.Text = _loc.Get(_lang, "pause.settings");
        _menuBtn.Text = _loc.Get(_lang, "pause.main_menu");
        _quitBtn.Text = _loc.Get(_lang, "pause.quit");
    }

    public void NotifySaveSucceeded()
    {
        if (_feedbackLabel is not null)
            _feedbackLabel.Text = _loc.Get(_lang, "pause.saved_ok");
    }

    public void Open()
    {
        if (_feedbackLabel is not null)
            _feedbackLabel.Text = string.Empty;
        Visible = true;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0.03f, 0.03f, 0.05f, 0.65f);
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 18);
        panel.CustomMinimumSize = new Vector2(360, 0);
        center.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        _titleLabel = UiThemeFactory.CreateHeader(_loc.Get(_lang, "pause.title"), 18);
        vbox.AddChild(_titleLabel);

        _resumeBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "pause.resume"), 0, 34, UiThemeFactory.GoldAccent);
        _resumeBtn.Pressed += () => { Visible = false; ResumeRequested?.Invoke(); };
        vbox.AddChild(_resumeBtn);

        _saveBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "pause.quicksave"), 0, 34);
        _saveBtn.Pressed += () => QuicksaveRequested?.Invoke();
        vbox.AddChild(_saveBtn);

        _settingsBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "pause.settings"), 0, 34);
        _settingsBtn.Pressed += () => SettingsRequested?.Invoke();
        vbox.AddChild(_settingsBtn);

        _menuBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "pause.main_menu"), 0, 34);
        _menuBtn.Pressed += () => { Visible = false; MainMenuRequested?.Invoke(); };
        vbox.AddChild(_menuBtn);

        _quitBtn = UiThemeFactory.CreateStyledButton(_loc.Get(_lang, "pause.quit"), 0, 34, UiThemeFactory.CrimsonAlert);
        _quitBtn.Pressed += () => GetTree().Quit();
        vbox.AddChild(_quitBtn);

        _feedbackLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = UiThemeFactory.EmeraldPositive };
        vbox.AddChild(_feedbackLabel);
    }
}
