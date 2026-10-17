using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DofusCompanion.App.ViewModels;
using DofusCompanion.Application.Settings;
using DofusCompanion.Domain;

namespace DofusCompanion.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ISettingsStore _settings;
    private Point? _dragStart;

    public MainWindow(MainViewModel vm, ISettingsStore settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        RestorePlacement();
        vm.Analysis.Candidates.CollectionChanged += (_, _) => DrawCandidateBoxes();
        PreviewImage.SizeChanged += (_, _) => DrawCandidateBoxes();
    }

    /// <summary>When true, closing the window really exits instead of only hiding it.</summary>
    public bool AllowClose { get; set; } = true;

    private void RestorePlacement()
    {
        var p = _settings.Current.MainWindow;
        Width = p.Width;
        Height = p.Height;
        if (!double.IsNaN(p.Left) && !double.IsNaN(p.Top) && OverlayWindow.IsOnScreen(p.Left, p.Top, p.Width))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var p = _settings.Current.MainWindow;
        if (WindowState == WindowState.Normal)
        {
            p.Left = Left;
            p.Top = Top;
            p.Width = Width;
            p.Height = Height;
        }

        try { _settings.Save(); }
        catch (IOException) { /* best effort on exit */ }
        base.OnClosing(e);
    }

    private void BrowserSearch_LostFocus(object sender, RoutedEventArgs e) => _vm.Browser.RememberQueryCommand.Execute(null);

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string q }) _vm.Browser.Query = q;
    }

    // ---------------------------------------------------------------- region selection on the preview

    private void PreviewImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.Analysis.HasImage) return;
        _dragStart = e.GetPosition(PreviewImage);
        PreviewImage.CaptureMouse();
        UpdateSelectionRect(_dragStart.Value, _dragStart.Value);
        SelectionRect.Visibility = Visibility.Visible;
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
            UpdateSelectionRect(start, e.GetPosition(PreviewImage));
    }

    private async void PreviewImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;
        PreviewImage.ReleaseMouseCapture();
        _dragStart = null;
        var end = e.GetPosition(PreviewImage);
        var rect = ToPixels(start, end);
        SelectionRect.Visibility = Visibility.Collapsed;
        if (rect is null || rect.Value.Width < 8 || rect.Value.Height < 8) return; // a click, not a drag
        _vm.Analysis.Region = rect;
        await _vm.Analysis.RunAsync();
    }

    private void UpdateSelectionRect(Point a, Point b)
    {
        Canvas.SetLeft(SelectionRect, Math.Min(a.X, b.X));
        Canvas.SetTop(SelectionRect, Math.Min(a.Y, b.Y));
        SelectionRect.Width = Math.Abs(a.X - b.X);
        SelectionRect.Height = Math.Abs(a.Y - b.Y);
    }

    /// <summary>Converts a drag on the scaled preview into source-image pixel coordinates.</summary>
    private PixelRect? ToPixels(Point a, Point b)
    {
        if (PreviewImage.ActualWidth <= 0 || _vm.Analysis.ImageWidth == 0) return null;
        var sx = _vm.Analysis.ImageWidth / PreviewImage.ActualWidth;
        var sy = _vm.Analysis.ImageHeight / PreviewImage.ActualHeight;
        var x = (int)(Math.Clamp(Math.Min(a.X, b.X), 0, PreviewImage.ActualWidth) * sx);
        var y = (int)(Math.Clamp(Math.Min(a.Y, b.Y), 0, PreviewImage.ActualHeight) * sy);
        var w = (int)(Math.Abs(a.X - b.X) * sx);
        var h = (int)(Math.Abs(a.Y - b.Y) * sy);
        return new PixelRect(x, y, Math.Min(w, _vm.Analysis.ImageWidth - x), Math.Min(h, _vm.Analysis.ImageHeight - y));
    }

    /// <summary>Outlines the text regions that produced each candidate (detected regions view).</summary>
    private void DrawCandidateBoxes()
    {
        CandidateBoxes.Children.Clear();
        if (PreviewImage.ActualWidth <= 0 || _vm.Analysis.ImageWidth == 0) return;
        var sx = PreviewImage.ActualWidth / _vm.Analysis.ImageWidth;
        var sy = PreviewImage.ActualHeight / _vm.Analysis.ImageHeight;
        foreach (var c in _vm.Analysis.Candidates)
        {
            if (c.Candidate.SourceBounds is not { } r) continue;
            var box = new Rectangle
            {
                Width = Math.Max(2, r.Width * sx),
                Height = Math.Max(2, r.Height * sy),
                Stroke = (Brush)FindResource(c.IsExact ? "Good" : "Caution"),
                StrokeThickness = 2,
                ToolTip = c.Name,
            };
            Canvas.SetLeft(box, r.X * sx);
            Canvas.SetTop(box, r.Y * sy);
            CandidateBoxes.Children.Add(box);
        }
    }
}
