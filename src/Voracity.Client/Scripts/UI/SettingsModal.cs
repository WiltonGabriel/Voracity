using Godot;
using Voracity.Core.Data;

namespace Voracity.Client.UI;

/// <summary>
/// Interactive Settings &amp; Language modal backed by Scenes/UI/SettingsModal.tscn.
/// Defaults to Visible = false so it never blocks gameplay at startup.
/// </summary>
public partial class SettingsModal : Control
{
    [Export] public Label TitleLabel { get; set; } = null!;
    [Export] public Label LangLabel { get; set; } = null!;
    [Export] public OptionButton LangOption { get; set; } = null!;
    [Export] public Label WindowLabel { get; set; } = null!;
    [Export] public OptionButton WindowOption { get; set; } = null!;
    [Export] public Label VsyncLabel { get; set; } = null!;
    [Export] public CheckButton VsyncToggle { get; set; } = null!;
    [Export] public Label ScaleLabel { get; set; } = null!;
    [Export] public OptionButton ScaleOption { get; set; } = null!;
    [Export] public Label SpeedLabel { get; set; } = null!;
    [Export] public SpinBox SpeedSpin { get; set; } = null!;
    [Export] public Label VolumeLabel { get; set; } = null!;
    [Export] public HSlider VolumeSlider { get; set; } = null!;
    [Export] public Label DebugLabel { get; set; } = null!;
    [Export] public CheckButton DebugToggle { get; set; } = null!;
    [Export] public Label DebugHintLabel { get; set; } = null!;
    [Export] public Button ApplyCloseButton { get; set; } = null!;

    private LocalizationCatalog? _loc;
    private GameSettings? _current;
    private bool _syncingUi;

    public event Action<GameSettings>? SettingsApplied;

    public void Initialize(LocalizationCatalog loc, GameSettings initial)
    {
        _loc = loc;
        _current = initial;
        Visible = false;
        if (IsNodeReady())
        {
            SyncControlsFromSettings();
            RefreshTexts();
        }
    }

    public override void _Ready()
    {
        Visible = false;
        TitleLabel ??= GetNode<Label>("Center/Panel/VBox/TitleLabel");
        LangLabel ??= GetNode<Label>("Center/Panel/VBox/Grid/LangLabel");
        LangOption ??= GetNode<OptionButton>("Center/Panel/VBox/Grid/LangOption");
        WindowLabel ??= GetNode<Label>("Center/Panel/VBox/Grid/WindowLabel");
        WindowOption ??= GetNode<OptionButton>("Center/Panel/VBox/Grid/WindowOption");
        VsyncLabel ??= GetNode<Label>("Center/Panel/VBox/Grid/VsyncLabel");
        VsyncToggle ??= GetNode<CheckButton>("Center/Panel/VBox/Grid/VsyncToggle");
        ScaleLabel ??= GetNode<Label>("Center/Panel/VBox/Grid/ScaleLabel");
        ScaleOption ??= GetNode<OptionButton>("Center/Panel/VBox/Grid/ScaleOption");
        SpeedLabel ??= GetNode<Label>("Center/Panel/VBox/Grid/SpeedLabel");
        SpeedSpin ??= GetNode<SpinBox>("Center/Panel/VBox/Grid/SpeedSpin");
        VolumeLabel ??= GetNode<Label>("Center/Panel/VBox/Grid/VolumeLabel");
        VolumeSlider ??= GetNode<HSlider>("Center/Panel/VBox/Grid/VolumeSlider");
        DebugLabel ??= GetNode<Label>("Center/Panel/VBox/Grid/DebugLabel");
        DebugToggle ??= GetNode<CheckButton>("Center/Panel/VBox/Grid/DebugToggle");
        DebugHintLabel ??= GetNode<Label>("Center/Panel/VBox/DebugHintLabel");
        ApplyCloseButton ??= GetNode<Button>("Center/Panel/VBox/ApplyCloseBtn");

        LangOption.ItemSelected += idx =>
        {
            if (_syncingUi || _current is null)
                return;
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

        WindowOption.ItemSelected += idx =>
        {
            if (_syncingUi || _current is null)
                return;
            var mode = idx switch
            {
                1 => "fullscreen",
                2 => "borderless",
                _ => "windowed"
            };
            _current = _current with { WindowMode = mode };
        };

        VsyncToggle.Toggled += pressed =>
        {
            if (_syncingUi || _current is null || _loc is null)
                return;
            _current = _current with { Vsync = pressed };
            var lang = _current.ResolveActiveLanguage();
            VsyncToggle.Text = pressed ? _loc.Get(lang, "settings.on") : _loc.Get(lang, "settings.off");
        };

        ScaleOption.ItemSelected += idx =>
        {
            if (_syncingUi || _current is null)
                return;
            var scale = idx switch { 1 => 1.15f, 2 => 1.30f, _ => 1.0f };
            _current = _current with { UiScale = scale };
        };

        SpeedSpin.ValueChanged += val =>
        {
            if (!_syncingUi && _current is not null)
                _current = _current with { DefaultSpeed = (int)val };
        };

        VolumeSlider.ValueChanged += val =>
        {
            if (!_syncingUi && _current is not null)
                _current = _current with { MasterVolume = (float)val };
        };

        DebugToggle.Toggled += pressed =>
        {
            if (_syncingUi || _current is null || _loc is null)
                return;
            _current = _current with { DebugMode = pressed };
            var lang = _current.ResolveActiveLanguage();
            DebugToggle.Text = pressed ? _loc.Get(lang, "settings.on") : _loc.Get(lang, "settings.off");
        };

        ApplyCloseButton.Pressed += () =>
        {
            ApplyAndEmit();
            Visible = false;
        };

        if (_loc is not null && _current is not null)
        {
            SyncControlsFromSettings();
            RefreshTexts();
        }
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
        if (_current is null || VsyncToggle is null)
            return;

        _syncingUi = true;
        VsyncToggle.ButtonPressed = _current.Vsync;
        SpeedSpin.Value = Math.Clamp(_current.DefaultSpeed, 1, 5);
        VolumeSlider.Value = Math.Clamp(_current.MasterVolume, 0.0f, 1.0f);
        DebugToggle.ButtonPressed = _current.DebugMode;
        ScaleOption.Selected = _current.UiScale >= 1.25f ? 2 : (_current.UiScale >= 1.10f ? 1 : 0);
        _syncingUi = false;
    }

    private void RefreshTexts()
    {
        if (_loc is null || _current is null || TitleLabel is null)
            return;

        _syncingUi = true;
        var lang = _current.ResolveActiveLanguage();
        var detectedSys = LocalizationCatalog.DetectSystemLanguage();
        var detectedName = detectedSys == LocalizationCatalog.PortugueseCode
            ? _loc.Get(lang, "settings.lang_pt")
            : _loc.Get(lang, "settings.lang_en");

        TitleLabel.Text = _loc.Get(lang, "settings.title");
        LangLabel.Text = _loc.Get(lang, "settings.language");
        WindowLabel.Text = _loc.Get(lang, "settings.window_mode");
        VsyncLabel.Text = _loc.Get(lang, "settings.vsync");
        ScaleLabel.Text = _loc.Get(lang, "settings.ui_scale");
        SpeedLabel.Text = _loc.Get(lang, "settings.default_speed");
        VolumeLabel.Text = _loc.Get(lang, "settings.volume");
        DebugLabel.Text = _loc.Get(lang, "settings.debug_mode");
        DebugHintLabel.Text = _loc.Get(lang, "settings.debug_hint");
        VsyncToggle.Text = _current.Vsync ? _loc.Get(lang, "settings.on") : _loc.Get(lang, "settings.off");
        DebugToggle.Text = _current.DebugMode ? _loc.Get(lang, "settings.on") : _loc.Get(lang, "settings.off");
        ApplyCloseButton.Text = _loc.Get(lang, "settings.save_close");

        LangOption.Clear();
        LangOption.AddItem(_loc.Format(lang, "settings.lang_auto", detectedName), 0);
        LangOption.AddItem(_loc.Get(lang, "settings.lang_pt"), 1);
        LangOption.AddItem(_loc.Get(lang, "settings.lang_en"), 2);
        LangOption.Selected = _current.LanguageMode switch
        {
            LocalizationCatalog.PortugueseCode => 1,
            LocalizationCatalog.EnglishCode => 2,
            _ => 0
        };

        WindowOption.Clear();
        WindowOption.AddItem(_loc.Get(lang, "settings.windowed"), 0);
        WindowOption.AddItem(_loc.Get(lang, "settings.fullscreen"), 1);
        WindowOption.AddItem(_loc.Get(lang, "settings.borderless"), 2);
        WindowOption.Selected = _current.WindowMode switch
        {
            "fullscreen" => 1,
            "borderless" => 2,
            _ => 0
        };

        if (ScaleOption.ItemCount == 0)
        {
            ScaleOption.AddItem("100%", 0);
            ScaleOption.AddItem("115%", 1);
            ScaleOption.AddItem("130%", 2);
        }
        _syncingUi = false;
    }

    private void ApplyAndEmit()
    {
        if (_current is null)
            return;
        SettingsStorage.Save(_current);
        SettingsApplied?.Invoke(_current);
    }
}
