using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace PlainCommander.App.Controls;

/// <summary>
/// The bar between the two panels. Dragging moves pixels from one star column to the other;
/// the columns' MinWidth keeps both panels usable. Own control (Thumb is sealed in WinUI, and the toolkit
/// GridSplitter package drags in MSIX build tools that break the build). Template lives in App.xaml.
/// </summary>
public sealed partial class PanelSplitter : Control
{
    private double _dragStartX;
    private double _dragStartLeft;
    private bool _dragging;

    public PanelSplitter()
    {
        DefaultStyleKey = typeof(PanelSplitter);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    /// <summary>The column to the left of the splitter.</summary>
    public ColumnDefinition? LeftColumn { get; set; }

    /// <summary>The column to the right of the splitter.</summary>
    public ColumnDefinition? RightColumn { get; set; }

    protected override void OnPointerEntered(PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "PointerOver", true);

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (LeftColumn is null || !CapturePointer(e.Pointer))
        {
            return;
        }

        _dragging = true;
        _dragStartX = e.GetCurrentPoint(null).Position.X;
        _dragStartLeft = LeftColumn.ActualWidth;
        VisualStateManager.GoToState(this, "Pressed", true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (!_dragging || LeftColumn is null || RightColumn is null)
        {
            return;
        }

        var total = LeftColumn.ActualWidth + RightColumn.ActualWidth;
        var delta = e.GetCurrentPoint(null).Position.X - _dragStartX;
        var left = Math.Clamp(_dragStartLeft + delta, LeftColumn.MinWidth, total - RightColumn.MinWidth);
        LeftColumn.Width = new GridLength(left, GridUnitType.Star);
        RightColumn.Width = new GridLength(total - left, GridUnitType.Star);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e) => EndDrag(e);

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) => EndDrag(e);

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }
}
