using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using VoiceFlow.Core.FlowBar;
using VoiceFlow.Desktop.Controls;

namespace VoiceFlow.Desktop.Views;

/// <summary>
/// The borderless always-on-top bar. It never takes focus. Dragging any visible part of it moves it, and a
/// right-click opens its menu; buttons still click as long as the pointer has not moved.
/// </summary>
public partial class FlowBarWindow : Window
{
    /// <summary>The pointer has to travel this far, in device-independent pixels, before a press becomes a drag.</summary>
    public const double DragThreshold = 4;

    Point? _pressedAt;
    bool _dragging;
    FlowBarLayout? _layout;

    public FlowBarWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Tunnel handlers see the press before a button does, so dragging works from anywhere on the bar.
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Only the window's own capture counts: a button losing it to the window is how a drag begins.
        AddHandler(PointerCaptureLostEvent, (_, e) => { if (ReferenceEquals(e.Source, this)) EndDrag(completed: false); },
                   RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>False while the user has locked the bar's position.</summary>
    public bool AllowDrag { get; set; } = true;

    public bool IsDragging => _dragging;

    /// <summary>The pointer moved past the threshold with the left button down. The argument is the pointer on screen.</summary>
    public event Action<PixelPoint>? DragStarted;
    public event Action<PixelPoint>? DragMoved;
    /// <summary>True when the button was released; false when the drag was interrupted.</summary>
    public event Action<bool>? DragEnded;
    /// <summary>A right-click on the bar.</summary>
    public event Action? MenuRequested;

    public FlowBarLayout? BarLayout => _layout ??= this.GetVisualDescendants().OfType<FlowBarLayout>().FirstOrDefault();

    /// <summary>The visible parts of the bar, in device-independent pixels from the window's top-left corner.</summary>
    public IReadOnlyList<RoundedBox> ShapeParts()
    {
        var parts = new List<RoundedBox>();
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            if (!control.Classes.Contains("barShape") || !control.IsEffectivelyVisible) continue;
            if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0) continue;
            if (control.TranslatePoint(default, this) is not { } origin) continue;
            var corners = control switch
            {
                Border b => b.CornerRadius,
                TemplatedControl t => t.CornerRadius,
                _ => default,
            };
            parts.Add(new RoundedBox(origin.X, origin.Y, control.Bounds.Width, control.Bounds.Height, corners.TopLeft));
        }
        return parts;
    }

    /// <summary>The capsule's box in the window (or the toast's, when it shows alone), in device-independent pixels.</summary>
    public Rect? AnchorRect()
    {
        if (BarLayout is not { } layout || layout.AnchorBox.Width <= 0) return null;
        return layout.TranslatePoint(layout.AnchorBox.TopLeft, this) is { } origin ? new Rect(origin, layout.AnchorBox.Size) : null;
    }

    /// <summary>Where the toast and menu open relative to the capsule.</summary>
    public void SetExtras(bool below, double offset)
    {
        if (BarLayout is not { } layout) return;
        if (layout.ExtrasBelow != below) layout.ExtrasBelow = below;
        if (Math.Abs(layout.ExtrasOffset - offset) >= 0.5) layout.ExtrasOffset = offset;
    }

    bool OnShape(Point local)
    {
        foreach (var part in ShapeParts())
            if (part.Contains(local.X, local.Y)) return true;
        return false;
    }

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!OnShape(point.Position)) return;
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            e.Handled = true;
            MenuRequested?.Invoke();
            return;
        }
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && AllowDrag) _pressedAt = point.Position;
    }

    void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedAt is not { } start) return;
        var position = e.GetPosition(this);
        if (!_dragging)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _pressedAt = null;
                return;
            }
            var moved = position - start;
            if (Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) < DragThreshold) return;
            _dragging = true;
            // Taking the capture from a pressed button cancels its click.
            e.Pointer.Capture(this);
            DragStarted?.Invoke(this.PointToScreen(start));
        }
        e.Handled = true;
        DragMoved?.Invoke(this.PointToScreen(position));
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedAt = null;
        if (!_dragging) return;
        e.Handled = true;
        EndDrag(completed: true);
        e.Pointer.Capture(null);
    }

    void EndDrag(bool completed)
    {
        _pressedAt = null;
        if (!_dragging) return;
        _dragging = false;
        DragEnded?.Invoke(completed);
    }
}
