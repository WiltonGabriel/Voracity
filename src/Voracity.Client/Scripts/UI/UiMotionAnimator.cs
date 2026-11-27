using Godot;

namespace Voracity.Client.UI;

/// <summary>
/// Dual-Mode UI micro-tween helper for smooth panel reveals, nested tooltips, and subtle button feedback.
/// Cancels any active tween tracked on the control to avoid overlapping transitions during rapid clicks.
/// </summary>
public static class UiMotionAnimator
{
    private const string TweenMetaKey = "_ui_motion_tween";
    private const string HoverOriginMetaKey = "_ui_hover_origin";

    /// <summary>
    /// Fades a panel in cleanly without overwriting its anchor offsets.
    /// </summary>
    public static void AnimateFadeIn(Control control, float duration = 0.12f, float startAlpha = 0.0f)
    {
        if (control is null || duration <= 0f || !control.IsInsideTree())
            return;

        KillExistingTween(control);
        var c = control.Modulate;
        control.Modulate = new Color(c.R, c.G, c.B, Mathf.Clamp(startAlpha, 0f, 1f));

        var tween = control.CreateTween();
        tween.SetEase(Tween.EaseType.Out);
        tween.SetTrans(Tween.TransitionType.Cubic);
        tween.TweenProperty(control, "modulate:a", 1.0f, duration);
        TrackTween(control, tween);
    }

    /// <summary>
    /// Fades and nudges a floating card/tooltip into place from a slight offset.
    /// </summary>
    public static void AnimatePopupIn(Control control, float duration = 0.10f, Vector2? offsetDelta = null)
    {
        if (control is null || duration <= 0f || !control.IsInsideTree())
            return;

        KillExistingTween(control);
        var targetPos = control.Position;
        var delta = offsetDelta ?? new Vector2(0f, 6f);
        control.Position = targetPos - delta;

        var c = control.Modulate;
        control.Modulate = new Color(c.R, c.G, c.B, 0f);

        var tween = control.CreateTween();
        tween.SetParallel(true);
        tween.SetEase(Tween.EaseType.Out);
        tween.SetTrans(Tween.TransitionType.Cubic);
        tween.TweenProperty(control, "modulate:a", 1.0f, duration);
        tween.TweenProperty(control, "position", targetPos, duration);
        TrackTween(control, tween);
    }

    /// <summary>
    /// Binds a subtle opacity/tint response on hover without shifting layout geometry.
    /// </summary>
    public static void BindSubtleHover(Button button, float duration = 0.08f, float hoverBoost = 1.08f)
    {
        if (button is null || button.HasMeta(HoverOriginMetaKey))
            return;

        button.SetMeta(HoverOriginMetaKey, button.SelfModulate);
        button.MouseEntered += () =>
        {
            if (button.Disabled || !button.IsInsideTree())
                return;
            var baseCol = button.GetMeta(HoverOriginMetaKey).AsColor();
            AnimateSelfModulate(button, new Color(
                Mathf.Min(1f, baseCol.R * hoverBoost),
                Mathf.Min(1f, baseCol.G * hoverBoost),
                Mathf.Min(1f, baseCol.B * hoverBoost),
                baseCol.A), duration);
        };
        button.MouseExited += () =>
        {
            if (!button.IsInsideTree())
                return;
            var baseCol = button.GetMeta(HoverOriginMetaKey).AsColor();
            AnimateSelfModulate(button, baseCol, duration);
        };
    }

    private static void AnimateSelfModulate(Control control, Color target, float duration)
    {
        KillExistingTween(control);
        if (duration <= 0f)
        {
            control.SelfModulate = target;
            return;
        }

        var tween = control.CreateTween();
        tween.SetEase(Tween.EaseType.Out);
        tween.SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(control, "self_modulate", target, duration);
        TrackTween(control, tween);
    }

    private static void KillExistingTween(Control control)
    {
        if (!control.HasMeta(TweenMetaKey))
            return;
        var obj = control.GetMeta(TweenMetaKey).AsGodotObject();
        if (obj is Tween existing && GodotObject.IsInstanceValid(existing))
            existing.Kill();
        control.RemoveMeta(TweenMetaKey);
    }

    private static void TrackTween(Control control, Tween tween)
    {
        control.SetMeta(TweenMetaKey, tween);
        tween.Finished += () =>
        {
            if (GodotObject.IsInstanceValid(control) && control.HasMeta(TweenMetaKey))
                control.RemoveMeta(TweenMetaKey);
        };
    }
}
