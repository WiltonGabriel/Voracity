using Godot;
using Voracity.Core.Data;

namespace Voracity.Client.UI;

/// <summary>
/// Interactive Settings &amp; Language window (supports Auto OS detection, pt-BR, en-US,
/// window mode, VSync, UI scale, simulation default speed, and volume).
/// </summary>
public partial class SettingsModal : Control
{
    private readonly LocalizationCatalog _loc;
    private GameSettings _current;

    private Label _titleLabel = null!;
    private Label _langLabel = null!;
    private OptionButton _langOption = null!;
    private Label _windowLabel = null!;
    private OptionButton _windowOption = null!;
    private Label _vsyncLabel = null!;
    private CheckButton _vsyncToggle = null!;
    private Label _scaleLabel = null!;
    private OptionButton _scaleOption = null!;
    private Label _speedLabel = null!;
    private SpinBox _speedSpin = null!;
    private Label _volumeLabel = null!;
    private HSlider _volumeSlider = null!;
    private Button _applyCloseButton = null!;

    public event Action<GameSettings>? SettingsApplied;

    public SettingsModal(LocalizationCatalog loc, GameSettings initial)
    {
        _loc = loc;
        _current = initial;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var dimmer = new ColorRect
        {
            Color = new Color(0.03f, 0.03f, 0.04f, 0.72f)
        };
        dimmer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dimmer);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = UiThemeFactory.CreatePanel(UiThemeFactory.PanelBg, UiThemeFactory.GoldAccent, 20);
        panel.CustomMinimumSize = new Vector2(540, 0);
        center.AddChild(panel);

        var vbox = new VBoxContainer { Theme = Theme };
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        _titleLabel = UiThemeFactory.CreateHeader(string.Empty, 22);
        vbox.AddChild(_titleLabel);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        vbox.AddChild(grid);

        _langLabel = new Label();
        _langOption = new OptionButton { CustomMinimumSize = new Vector2(260, 34) };
        _langOption.ItemSelected += idx =>
        {
            var mode = idx switch
            {
                1 => LocalizationCatalog.PortugueseCode,
                2 => LocalizationCatalog.EnglishCode,
                _ => "auto"
            };
            _current = _current with { LanguageMode = mode };
            RefreshTexts();
            ApplyAndEmit();
        };
        grid.AddChild(_langLabel);
        grid.AddChild(_langOption);

        _windowLabel = new Label();
        _windowOption = new OptionButton { CustomMinimumSize = new Vector2(260, 34) };
        _windowOption.ItemSelected += idx =>
        {
            var mode = idx switch
            {
                1 => "fullscreen",
                2 => "borderless",
                _ => "windowed"
            };
            _current = _current with { WindowMode = mode };
        };
        grid.AddChild(_windowLabel);
        grid.AddChild(_windowOption);

        _vsyncLabel = new Label();
        _vsyncToggle = new CheckButton();
        _vsyncToggle.Toggled += pressed => _current = _current with { Vsync = pressed };
        grid.AddChild(_vsyncLabel);
        grid.AddChild(_vsyncToggle);

        _scaleLabel = new Label();
        _scaleOption = new OptionButton { CustomMinimumSize = new Vector2(260, 34) };
        _scaleOption.AddItem("100%", 0);
        _scaleOption.AddItem("115%", 1);
        _scaleOption.AddItem("130%", 2);
        _scaleOption.ItemSelected += idx =>
        {
            var scale = idx switch { 1 => 1.15f, 2 => 1.30f, _ => 1.0f };
            _current = _current with { UiScale = scale };
        };
        grid.AddChild(_scaleLabel);
        grid.AddChild(_scaleOption);

        _speedLabel = new Label();
        _speedSpin = new SpinBox { MinValue = 1, MaxValue = 5, Step = 1 };
        _speedSpin.ValueChanged += val => _current = _current with { DefaultSpeed = (int)val };
        grid.AddChild(_speedLabel);
        grid.AddChild(_speedSpin);

        _volumeLabel = new Label();
        _volumeSlider = new HSlider { MinValue = 0.0, MaxValue = 1.0, Step = 0.05, CustomMinimumSize = new Vector2(240, 28) };
        _volumeSlider.ValueChanged += val => _current = _current with { MasterVolume = (float)val };
        grid.AddChild(_volumeLabel);
        grid.AddChild(_volumeSlider);

        _applyCloseButton = new Button { CustomMinimumSize = new Vector2(0, 40) };
        _applyCloseButton.Pressed += () =>
        {
            ApplyAndEmit();
            Visible = false;
        };
        vbox.AddChild(_applyCloseButton);

        SyncControlsFromSettings();
        RefreshTexts();
    }

    public void Open(GameSettings latest)
    {
        _current = latest;
        SyncControlsFromSettings();
        RefreshTexts();
        Visible = true;
    }

    private void SyncControlsFromSettings()
    {
        _vsyncToggle.ButtonPressed = _current.Vsync;
        _speedSpin.Value = Math.Clamp(_current.DefaultSpeed, 1, 5);
        _volumeSlider.Value = Math.Clamp(_current.MasterVolume, 0.0f, 1.0f);
        _scaleOption.Selected = _current.UiScale >= 1.25f ? 2 : (_current.UiScale >= 1.10f ? 1 : 0);
    }

    private void RefreshTexts()
    {
        var lang = _current.ResolveActiveLanguage();
        var detectedSys = LocalizationCatalog.DetectSystemLanguage();
        var detectedName = detectedSys == LocalizationCatalog.PortugueseCode
            ? _loc.Get(lang, "settings.lang_pt")
            : _loc.Get(lang, "settings.lang_en");

        _titleLabel.Text = _loc.Get(lang, "settings.title");
        _langLabel.Text = _loc.Get(lang, "settings.language");
        _windowLabel.Text = _loc.Get(lang, "settings.window_mode");
        _vsyncLabel.Text = _loc.Get(lang, "settings.vsync");
        _scaleLabel.Text = _loc.Get(lang, "settings.ui_scale");
        _speedLabel.Text = _loc.Get(lang, "settings.default_speed");
        _volumeLabel.Text = _loc.Get(lang, "settings.volume");
        _vsyncToggle.Text = _current.Vsync ? _loc.Get(lang, "settings.on") : _loc.Get(lang, "settings.off");
        _applyCloseButton.Text = _loc.Get(lang, "settings.save_close");

        _langOption.Clear();
        _langOption.AddItem(_loc.Format(lang, "settings.lang_auto", detectedName), 0);
        _langOption.AddItem(_loc.Get(lang, "settings.lang_pt"), 1);
        _langOption.AddItem(_loc.Get(lang, "settings.lang_en"), 2);
        _langOption.Selected = _current.LanguageMode switch
        {
            LocalizationCatalog.PortugueseCode => 1,
            LocalizationCatalog.EnglishCode => 2,
            _ => 0
        };

        _windowOption.Clear();
        _windowOption.AddItem(_loc.Get(lang, "settings.windowed"), 0);
        _windowOption.AddItem(_loc.Get(lang, "settings.fullscreen"), 1);
        _windowOption.AddItem(_loc.Get(lang, "settings.borderless"), 2);
        _windowOption.Selected = _current.WindowMode switch
        {
            "fullscreen" => 1,
            "borderless" => 2,
            _ => 0
        };
    }

    private void ApplyAndEmit()
    {
        SettingsStorage.Save(_current);
        SettingsApplied?.Invoke(_current);
    }
}
