using Godot;

namespace Voracity.Client.UI;

/// <summary>
/// Dual-Mode Nested Tooltip card backed by Scenes/UI/NestedTooltipCard.tscn.
/// Supports visual editing in the Godot Inspector and runtime Action Lock feedback.
/// </summary>
public partial class NestedTooltipCardView : PanelContainer
{
    [Export] public Label TitleLabel { get; set; } = null!;
    [Export] public TextureRect LockIcon { get; set; } = null!;
    [Export] public Label LockHintLabel { get; set; } = null!;
    [Export] public RichTextLabel BodyRichText { get; set; } = null!;

    [ExportGroup("Lock Feedback")]
    [Export] public Texture2D? UnlockedIcon { get; set; }
    [Export] public Texture2D? LockedIcon { get; set; }
    [Export] public Color UnlockedAccentColor { get; set; } = new(0.549f, 0.596f, 0.682f, 1.0f);
    [Export] public Color LockedAccentColor { get; set; } = new(0.490f, 0.427f, 0.780f, 1.0f);

    [Signal]
    public delegate void ConceptLinkClickedEventHandler(string conceptId, int level);

    public event Action<string, int, NestedTooltipCardView>? ConceptMetaClicked;

    private int _level;

    public override void _Ready()
    {
        TitleLabel ??= GetNode<Label>("VBox/HeaderRow/TitleLabel");
        LockIcon ??= GetNode<TextureRect>("VBox/HeaderRow/LockIcon");
        LockHintLabel ??= GetNode<Label>("VBox/HeaderRow/LockHintLabel");
        BodyRichText ??= GetNode<RichTextLabel>("VBox/BodyRichText");

        UnlockedIcon ??= GD.Load<Texture2D>("res://Assets/Icons/Ui/unlock.svg");
        LockedIcon ??= GD.Load<Texture2D>("res://Assets/Icons/Ui/lock.svg");

        BodyRichText.MetaClicked += meta =>
        {
            var conceptId = meta.AsString().Replace("concept:", string.Empty);
            EmitSignal(SignalName.ConceptLinkClicked, conceptId, _level + 1);
            ConceptMetaClicked?.Invoke(conceptId, _level + 1, this);
        };
    }

    public void Configure(string title, string bodyBbcode, string lockHint, int level, bool isLocked)
    {
        _level = level;
        if (TitleLabel is not null)
            TitleLabel.Text = title;
        if (LockHintLabel is not null)
            LockHintLabel.Text = lockHint;
        if (BodyRichText is not null)
            BodyRichText.Text = bodyBbcode;

        SetLocked(isLocked);
    }

    public void SetLocked(bool isLocked)
    {
        if (LockIcon is not null)
        {
            LockIcon.Texture = isLocked ? LockedIcon : UnlockedIcon;
            LockIcon.SelfModulate = isLocked ? LockedAccentColor : UnlockedAccentColor;
        }
        if (LockHintLabel is not null)
            LockHintLabel.Modulate = isLocked ? LockedAccentColor : Colors.White;
    }
}
