using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PlainCommander.App.Helpers;
using PlainCommander.Core.Viewer;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>F3. Keys as in Lister: 1 text, 2 hex, 3 image, W wrap, Esc or F3 closes.</summary>
public sealed partial class ViewerWindow : Window
{
    public ViewerWindow(string path)
    {
        ViewModel = new ViewerViewModel(path);
        InitializeComponent();
        Title = $"{ViewModel.FileName} - Plain Commander";
        Ui.FollowAppTheme(this, Root);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        Ui.ApplyIcon(this);
        TitleRow.Loaded += (_, _) => UpdateCaptionPadding();
        TitleRow.SizeChanged += (_, _) => UpdateCaptionPadding();
        PlaceOnScreen();
        FillEncodings();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.Dispose();
        };
        Root.Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            WindowFront.BringIfBehind(this); // a slow (cloud) load may have let another window take the front meanwhile
        };
    }

    public ViewerViewModel ViewModel { get; }

    private void PlaceOnScreen()
    {
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(1400, work.Width * 80 / 100);
        var height = Math.Min(1000, work.Height * 85 / 100);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
    }

    private void UpdateCaptionPadding()
    {
        var scale = TitleRow.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private void FillEncodings()
    {
        foreach (var encoding in ViewerViewModel.Encodings)
        {
            var item = new MenuFlyoutItem { Text = Ui.EncodingName(encoding) };
            var chosen = encoding;
            item.Click += async (_, _) => await ViewModel.SetEncodingAsync(chosen);
            EncodingFlyout.Items.Add(item);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewerViewModel.Mode))
        {
            return;
        }

        if (ViewModel.Mode == ViewerMode.Image && ImageView.Source is null)
        {
            // The image decoder lives in the view; the file is read once, when the image view is first shown.
            ImageView.Source = new BitmapImage(new Uri(ViewModel.Path));
        }

        // The keyboard (PageDown, End, arrows) belongs to the view that is showing.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            switch (ViewModel.Mode)
            {
                case ViewerMode.Text:
                    TextView.Focus(FocusState.Programmatic);
                    break;
                case ViewerMode.Hex:
                    Root.Focus(FocusState.Programmatic); // keys are handled by the window
                    break;
                default:
                    Root.Focus(FocusState.Programmatic);
                    break;
            }
        });
    }

    private const double HexRowHeight = 20;

    private void OnHexSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.HexVisibleLines = Math.Max(1, (int)((e.NewSize.Height - 12) / HexRowHeight));

    private void OnHexScroll(object sender, Microsoft.UI.Xaml.Controls.Primitives.ScrollEventArgs e) =>
        ViewModel.HexScrollTo((long)Math.Round(e.NewValue));

    private void OnHexWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(HexHost).Properties.MouseWheelDelta;
        ViewModel.HexScrollBy(-delta / 120 * 3);
        e.Handled = true;
    }

    private void OnTextMode(object sender, RoutedEventArgs e) => _ = ViewModel.SetModeAsync(ViewerMode.Text);

    private void OnHexMode(object sender, RoutedEventArgs e) => _ = ViewModel.SetModeAsync(ViewerMode.Hex);

    private void OnImageMode(object sender, RoutedEventArgs e) => _ = ViewModel.SetModeAsync(ViewerMode.Image);

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The content itself is a read-only TextBox and has focus after F3; only an editable box takes digits.
        var inText = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox { IsReadOnly: false };
        switch (e.Key)
        {
            case VirtualKey.Escape:
            case VirtualKey.F3:
                e.Handled = true;
                Close();
                break;
            case VirtualKey.Number1 or VirtualKey.NumberPad1 when !inText:
                e.Handled = true;
                _ = ViewModel.SetModeAsync(ViewerMode.Text);
                break;
            case VirtualKey.Number2 or VirtualKey.NumberPad2 when !inText:
                e.Handled = true;
                _ = ViewModel.SetModeAsync(ViewerMode.Hex);
                break;
            case VirtualKey.Number3 or VirtualKey.NumberPad3 when !inText && ViewModel.CanShowImage:
                e.Handled = true;
                _ = ViewModel.SetModeAsync(ViewerMode.Image);
                break;
            case VirtualKey.W when !inText:
                e.Handled = true;
                ViewModel.WordWrap = !ViewModel.WordWrap;
                break;
            case VirtualKey.Down when ViewModel.IsHex: e.Handled = true; ViewModel.HexScrollBy(1); break;
            case VirtualKey.Up when ViewModel.IsHex: e.Handled = true; ViewModel.HexScrollBy(-1); break;
            case VirtualKey.PageDown when ViewModel.IsHex: e.Handled = true; ViewModel.HexPageDown(); break;
            case VirtualKey.PageUp when ViewModel.IsHex: e.Handled = true; ViewModel.HexPageUp(); break;
            case VirtualKey.Home when ViewModel.IsHex: e.Handled = true; ViewModel.HexHome(); break;
            case VirtualKey.End when ViewModel.IsHex: e.Handled = true; ViewModel.HexEnd(); break;
        }
    }
}
