using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DofusCompanion.App.Services;
using DofusCompanion.App.ViewModels;
using DofusCompanion.Application.Settings;

namespace DofusCompanion.App.Views;

/// <summary>
/// Borderless always-on-top panel. Window-level behaviours that are inherently Win32 (no-activate,
/// click-through, capture exclusion, placement persistence) live here; content logic is in the view model.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly ISettingsStore _settings;

    public OverlayWindow(OverlayViewModel vm, ISettingsStore settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        RestorePlacement();
        SourceInitialized += (_, _) => ApplyWindowStyles();
        LocationChanged += (_, _) => SavePlacementDeferred();
        SizeChanged += (_, _) => SavePlacementDeferred();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OverlayViewModel.Locked)) ResizeMode = vm.Locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
            if (e.PropertyName is nameof(OverlayViewModel.ClickThrough))
            {
                ClickThroughBanner.Text = Localization.Loc.Instance.Format("Overlay.ClickThroughOn",
                    string.IsNullOrWhiteSpace(_settings.Current.HotkeyToggleClickThrough) ? "—" : _settings.Current.HotkeyToggleClickThrough);
                ApplyWindowStyles();
            }
        };
        ResizeMode = vm.Locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
    }

    /// <summary>Re-applies opacity/size/flags after settings were saved.</summary>
    public void ApplySettings()
    {
        _vm.Opacity = _settings.Current.OverlayOpacity;
        Width = _settings.Current.Overlay.Width;
        Height = _settings.Current.Overlay.Height;
        ApplyWindowStyles();
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW; // keep it out of Alt+Tab
        ex = _settings.Current.OverlayNoActivate ? ex | NativeMethods.WS_EX_NOACTIVATE : ex & ~NativeMethods.WS_EX_NOACTIVATE;
        ex = _vm.ClickThrough ? ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)ex);
        // Always visible to screen sharing (Discord, OBS…); hidden only during our own capture (see below).
        ScreenCaptureService.ExcludeFromCapture(this, false);
    }

    /// <summary>
    /// Excludes the overlay from screen captures until the returned scope is disposed, so Dofus Companion's own
    /// capture never contains (and OCRs) the overlay while Discord/OBS still see it the rest of the time.
    /// </summary>
    public async Task<IDisposable> ExcludeFromCaptureScopeAsync()
    {
        if (!IsVisible || !ScreenCaptureService.ExcludeFromCapture(this, true))
            return new Scope(() => { });
        await Task.Delay(120); // let the compositor apply the affinity before pixels are copied
        return new Scope(() => ScreenCaptureService.ExcludeFromCapture(this, false));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Locked || e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Button>(d) is not null) return;
        DragMove();
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null and not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _vm.Results.Count > 0)
        {
            // Move keyboard focus to the first result button; Enter/Space then picks it.
            ResultsList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.Results.Count > 0)
        {
            _vm.PickCommand.Execute(_vm.Results[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.Query.Length > 0)
        {
            _vm.Query = "";
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- placement

    private void RestorePlacement()
    {
        var p = _settings.Current.Overlay;
        Width = p.Width;
        Height = p.Height;
        if (double.IsNaN(p.Left) || double.IsNaN(p.Top) || !IsOnScreen(p.Left, p.Top, p.Width))
        {
            // First run, or the saved monitor is gone: top-right of the primary work area.
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - p.Width - 24;
            Top = wa.Top + 80;
        }
        else
        {
            Left = p.Left;
            Top = p.Top;
        }
    }

    /// <summary>True if at least the title bar would be visible on the current virtual screen (multi-monitor).</summary>
    internal static bool IsOnScreen(double left, double top, double width) =>
        left + width - 60 > SystemParameters.VirtualScreenLeft
        && left + 60 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
        && top >= SystemParameters.VirtualScreenTop - 10
        && top + 30 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    private void SavePlacementDeferred()
    {
        if (!IsLoaded) return;
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(600), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _saveTimer!.Stop();
                var p = _settings.Current.Overlay;
                p.Left = Left;
                p.Top = Top;
                p.Width = ActualWidth;
                p.Height = ActualHeight;
                _settings.Save();
            }, Dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
