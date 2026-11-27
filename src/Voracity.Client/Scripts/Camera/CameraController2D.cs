using Godot;
using Voracity.Client.Map;

namespace Voracity.Client.Camera;

/// <summary>
/// Manages Godot 2D strategic camera panning (WASD/Arrows + Right/Middle drag),
/// scroll-wheel zoom, clamping, and world focus.
/// </summary>
public sealed partial class CameraController2D : Camera2D
{
    private readonly Vector2 _worldCanvasSize;
    private readonly float _minZoom;
    private readonly float _maxZoom;
    private bool _draggingCamera;

    public event Action<float>? ZoomChanged;

    public CameraController2D() : this(MapView2D.WorldCanvasSize, MapView2D.MinCameraZoom, MapView2D.MaxCameraZoom)
    {
    }

    public CameraController2D(Vector2 worldCanvasSize, float minZoom, float maxZoom)
    {
        _worldCanvasSize = worldCanvasSize;
        _minZoom = minZoom;
        _maxZoom = maxZoom;
        Position = _worldCanvasSize * 0.50f;
        Zoom = new Vector2(0.84f, 0.84f);
        LimitLeft = -1100;
        LimitTop = -800;
        LimitRight = (int)_worldCanvasSize.X + 1100;
        LimitBottom = (int)_worldCanvasSize.Y + 800;
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
        var next = Mathf.Clamp(zoom, _minZoom, _maxZoom);
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
            Position += dir.Normalized() * (float)(650.0 * delta) / Zoom.X;
            ClampPosition();
        }
    }

    public bool HandlePointerInput(InputEvent evt)
    {
        if (evt is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            {
                AdjustZoom(1.12f);
                return true;
            }
            if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            {
                AdjustZoom(0.89f);
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
            Mathf.Clamp(Position.X, -560f, _worldCanvasSize.X + 560f),
            Mathf.Clamp(Position.Y, -360f, _worldCanvasSize.Y + 360f));
    }
}
