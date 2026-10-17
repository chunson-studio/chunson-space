using FAswitch.Windows;

namespace FAswitch.Desktop;

public sealed class WindowsTrayHost : ITrayHost
{
    private readonly System.Windows.Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon brandIcon;
    public WindowsTrayHost()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示 Chunson Space", null, (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        brandIcon = BrandIcon.CreateTrayIcon();
        icon = new() { Icon = brandIcon, Text = "Chunson Space", ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;
    public void Show(string tooltip) { icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; icon.Visible = true; }
    public void Hide() => icon.Visible = false;
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); brandIcon.Dispose(); }
}
