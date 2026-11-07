using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>
/// Ctrl+M. The preview updates as you type; Enter or "Rename" runs it when nothing is wrong; Undo reverses
/// the last run; Esc closes. Placeholders are inserted at the caret of the mask field that had focus last.
/// </summary>
public sealed partial class MultiRenameWindow : Window
{
    private static readonly (string Token, string Key)[] Tokens =
    [
        ("[N]", "Mr_TokName"), ("[N1-3]", "Mr_TokNameRange"), ("[E]", "Mr_TokExt"), ("[C]", "Mr_TokCounter"),
        ("[P]", "Mr_TokParent"), ("[YMD]", "Mr_TokDate"), ("[hms]", "Mr_TokTime"),
    ];

    private readonly MainViewModel _main;
    private TextBox _lastMask;

    public MultiRenameWindow(MainViewModel main, MultiRenameViewModel viewModel)
    {
        _main = main;
        ViewModel = viewModel;
        InitializeComponent();
        _lastMask = NameBox;
        Title = Loc.Get("Mr_Title") + " - Plain Commander";
        TitleText.Text = Loc.Format("Mr_TitleCount", viewModel.Count);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        Ui.ApplyIcon(this);
        Ui.FollowAppTheme(this, Root);
        TitleRow.Loaded += (_, _) => UpdateCaptionPadding();
        TitleRow.SizeChanged += (_, _) => UpdateCaptionPadding();
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(1000, work.Width * 75 / 100);
        var height = Math.Min(720, work.Height * 80 / 100);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));

        foreach (var (token, key) in Tokens)
        {
            var item = new MenuFlyoutItem { Text = $"{token}   {Loc.Get(key)}" };
            item.Click += (_, _) => Insert(token);
            InsertMenu.Items.Add(item);
        }

        foreach (var key in new[] { "Mr_CaseKeep", "Mr_CaseLower", "Mr_CaseUpper", "Mr_CaseFirst", "Mr_CaseWords" })
        {
            CaseBox.Items.Add(Loc.Get(key));
        }

        // NumberBox and ComboBox are filled here, not through TwoWay x:Bind: they push empty values back while loading.
        Root.Loaded += (_, _) =>
        {
            CaseBox.SelectedIndex = ViewModel.CaseIndex;
            StartBox.Value = ViewModel.CounterStart;
            StepBox.Value = ViewModel.CounterStep;
            DigitsBox.Value = ViewModel.CounterDigits;
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectAll();
        };
    }

    public MultiRenameViewModel ViewModel { get; }

    private void UpdateCaptionPadding()
    {
        var scale = TitleRow.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private void OnMaskFocus(object sender, RoutedEventArgs e) => _lastMask = (TextBox)sender;

    private void Insert(string token)
    {
        var box = _lastMask;
        var at = box.SelectionStart;
        box.Text = box.Text.Remove(at, box.SelectionLength).Insert(at, token);
        box.Focus(FocusState.Programmatic);
        box.SelectionStart = at + token.Length;
    }

    private void OnCaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaseBox.SelectedIndex >= 0)
        {
            ViewModel.CaseIndex = CaseBox.SelectedIndex;
        }
    }

    private void OnCounterChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return; // cleared box: keep the last good value
        }

        if (sender == StartBox)
        {
            ViewModel.CounterStart = args.NewValue;
        }
        else if (sender == StepBox)
        {
            ViewModel.CounterStep = args.NewValue;
        }
        else
        {
            ViewModel.CounterDigits = args.NewValue;
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.ApplyAsync() is { } outcome)
        {
            await _main.AfterMultiRenameAsync(outcome);
            CaseBox.SelectedIndex = 0;
            Activate(); // keep Undo in reach after the panels refresh
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.UndoAsync() is { } outcome)
        {
            await _main.AfterMultiRenameAsync(outcome);
            Activate();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;
            case VirtualKey.Enter when ViewModel.CanApply:
                e.Handled = true;
                OnApply(sender, e);
                break;
        }
    }
}
