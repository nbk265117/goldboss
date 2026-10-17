using DofusCompanion.App.Localization;
using Forms = System.Windows.Forms;

namespace DofusCompanion.App.Services;

/// <summary>
/// Icon in the Windows notification area (next to the clock): click toggles the overlay, double-click opens
/// the main window, right-click shows a menu. It only controls the companion itself.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _overlayItem;
    private readonly Forms.ToolStripMenuItem _mainItem;
    private readonly Forms.ToolStripMenuItem _clickThroughItem;
    private readonly Forms.ToolStripMenuItem _quitItem;
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer;

    public TrayIconService()
    {
        _overlayItem = new Forms.ToolStripMenuItem("", null, (_, _) => ToggleOverlay?.Invoke(this, EventArgs.Empty));
        _mainItem = new Forms.ToolStripMenuItem("", null, (_, _) => OpenMain?.Invoke(this, EventArgs.Empty));
        _clickThroughItem = new Forms.ToolStripMenuItem("", null, (_, _) => ToggleClickThrough?.Invoke(this, EventArgs.Empty));
        _quitItem = new Forms.ToolStripMenuItem("", null, (_, _) => Quit?.Invoke(this, EventArgs.Empty));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([_overlayItem, _mainItem, _clickThroughItem, new Forms.ToolStripSeparator(), _quitItem]);

        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), ContextMenuStrip = menu, Visible = false };

        // A double-click also raises a click: wait the system double-click time before treating it as a click.
        _clickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            ToggleOverlay?.Invoke(this, EventArgs.Empty);
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _clickTimer.Start(); };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            _clickTimer.Stop();
            OpenMain?.Invoke(this, EventArgs.Empty);
        };

        Loc.Instance.LanguageChanged += (_, _) => UpdateTexts();
        UpdateTexts();
    }

    public event EventHandler? ToggleOverlay;
    public event EventHandler? OpenMain;
    public event EventHandler? ToggleClickThrough;
    public event EventHandler? Quit;

    public void Show() => _icon.Visible = true;

    /// <summary>Short notification bubble (e.g. "still running here" the first time the window is closed).</summary>
    public void Notify(string text) => _icon.ShowBalloonTip(4000, AppInfo.Name, text, Forms.ToolTipIcon.Info);

    private void UpdateTexts()
    {
        _icon.Text = AppInfo.Title; // tooltip, max 63 chars
        _overlayItem.Text = Loc.Instance["Tray.Overlay"];
        _mainItem.Text = Loc.Instance["Tray.Main"];
        _clickThroughItem.Text = Loc.Instance["Tray.ClickThrough"];
        _quitItem.Text = Loc.Instance["Tray.Quit"];
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/DofusGoldBoss;component/Assets/app.ico"));
        return info is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(info.Stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _clickTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
