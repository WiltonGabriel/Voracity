using Godot;
using Voracity.Client.Map;

namespace Voracity.Client.Camera;

/// <summary>
/// Manages Godot 2D strategic camera panning (WASD/Arrows + Right/Middle drag),
/// scroll-wheel zoom, clamping, and world focus. All camera parameters are Inspector-editable via [Export].
/// </summary>
public sealed partial class CameraController2D : Camera2D
{
    [ExportGroup("Zoom & Pan")]
    [Export(PropertyHint.Range, "0.15,1.5,0.02")]
    public float MinZoom { get; set; } = MapView2D.MinCameraZoom;

    [Export(PropertyHint.Range, "1.5,10.0,0.1")]
    public float MaxZoom { get; set; } = MapView2D.MaxCameraZoom;

    [Export(PropertyHint.Range, "0.2,3.0,0.02")]
    public float DefaultZoom { get; set; } = 0.84f;

    [Export(PropertyHint.Range, "100.0,2000.0,25.0")]
    public float PanSpeed { get; set; } = 650.0f;

    [Export(PropertyHint.Range, "1.02,1.5,0.01")]
    public float WheelZoomInFactor { get; set; } = 1.12f;

    [Export(PropertyHint.Range, "0.60,0.98,0.01")]
    public float WheelZoomOutFactor { get; set; } = 0.89f;

    [ExportGroup("Bounds")]
    [Export]
    public Vector2 WorldCanvasSize { get; set; } = MapView2D.WorldCanvasSize;

    [Export]
    public float ClampMarginX { get; set; } = 560f;

    [Export]
    public float ClampMarginY { get; set; } = 360f;

    [Export]
    public int CameraLimitPaddingX { get; set; } = 1100;

    [Export]
    public int CameraLimitPaddingY { get; set; } = 800;

    private bool _draggingCamera;

    public event Action<float>? ZoomChanged;

    public override void _Ready()
    {
        Position = WorldCanvasSize * 0.50f;
        var initialZoom = Mathf.Clamp(DefaultZoom, MinZoom, MaxZoom);
        Zoom = new Vector2(initialZoom, initialZoom);
        LimitLeft = -CameraLimitPaddingX;
        LimitTop = -CameraLimitPaddingY;
        LimitRight = (int)WorldCanvasSize.X + CameraLimitPaddingX;
        LimitBottom = (int)WorldCanvasSize.Y + CameraLimitPaddingY;
        Enabled = true;
    }

    public void FocusWorldPosition(Vector2 worldPos, float? zoom = null)
    {
        Position = worldPos;
        ClampPosition();
        if (zoom.HasValue)
            SetZoom(zoom.Value);
    }

    public void SetZoom(float zoom)
    {
        var next = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        Zoom = new Vector2(next, next);
        ZoomChanged?.Invoke(next);
    }

    public void AdjustZoom(float factor) => SetZoom(Zoom.X * factor);

    public void HandleKeyboardPan(double delta)
    {
        var dir = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) dir.Y -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) dir.Y += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) dir.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) dir.X += 1;

        if (dir != Vector2.Zero)
        {
            Position += dir.Normalized() * (float)(PanSpeed * delta) / Zoom.X;
            ClampPosition();
        }
    }

    public bool HandlePointerInput(InputEvent evt)
    {
        if (evt is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            {
                AdjustZoom(WheelZoomInFactor);
                return true;
            }
            if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            {
                AdjustZoom(WheelZoomOutFactor);
                return true;
            }
            if (mb.ButtonIndex is MouseButton.Right or MouseButton.Middle)
            {
                _draggingCamera = mb.Pressed;
                return true;
            }
        }
        else if (evt is InputEventMouseMotion motion && _draggingCamera)
        {
            Position -= motion.Relative / Zoom;
            ClampPosition();
            return true;
        }

        return false;
    }

    private void ClampPosition()
    {
        Position = new Vector2(
            Mathf.Clamp(Position.X, -ClampMarginX, WorldCanvasSize.X + ClampMarginX),
            Mathf.Clamp(Position.Y, -ClampMarginY, WorldCanvasSize.Y + ClampMarginY));
    }
}
