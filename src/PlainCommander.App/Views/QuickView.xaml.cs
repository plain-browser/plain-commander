using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PlainCommander.Core.Viewer;
using PlainCommander.Presentation;

namespace PlainCommander.App.Views;

/// <summary>Ctrl+Q pane. The view model picks the file; images are decoded here, as in the F3 window.</summary>
public sealed partial class QuickView : UserControl
{
    private const double HexRowHeight = 20;
    private ViewerViewModel? _viewer;

    public QuickView()
    {
        InitializeComponent();

        HexHost.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnHexWheel), handledEventsToo: true);
    }

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(QuickViewModel), typeof(QuickView), new PropertyMetadata(null, OnViewModelChanged));

    public QuickViewModel ViewModel
    {
        get => (QuickViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (QuickView)d;
        if (e.OldValue is QuickViewModel old)
        {
            old.PropertyChanged -= view.OnQuickViewChanged;
        }

        if (e.NewValue is QuickViewModel vm)
        {
            vm.PropertyChanged += view.OnQuickViewChanged;
        }
    }

    private void OnQuickViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickViewModel.Viewer))
        {
            return;
        }

        if (_viewer is not null)
        {
            _viewer.PropertyChanged -= OnViewerChanged;
        }

        _viewer = ViewModel.Viewer;
        ImageView.Source = null;
        if (_viewer is not null)
        {
            _viewer.PropertyChanged += OnViewerChanged;
            _viewer.HexVisibleLines = HexLines(HexHost.ActualHeight);
            _viewer.HexBytesPerLine = BytesPerLine(ActualWidth);
        }
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.Mode) && sender is ViewerViewModel { Mode: ViewerMode.Image } viewer && ReferenceEquals(viewer, _viewer))
        {
            ImageView.Source = new BitmapImage(new Uri(viewer.Path));
        }
    }

    private static int HexLines(double height) => Math.Max(1, (int)((height - 12) / HexRowHeight));

    /// <summary>A 16-byte row of Cascadia Mono 13 is about 600 px wide.</summary>
    private static int BytesPerLine(double width) => width >= 620 ? 16 : 8;

    private void OnHexSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewer is not null)
        {
            _viewer.HexVisibleLines = HexLines(e.NewSize.Height);
            _viewer.HexBytesPerLine = BytesPerLine(e.NewSize.Width);
        }
    }

    private void OnHexWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(HexHost).Properties;
        if (point.IsHorizontalMouseWheel)
        {
            return;
        }

        _viewer?.HexScrollBy(-point.MouseWheelDelta / 120 * 3);
        e.Handled = true;
    }
}
