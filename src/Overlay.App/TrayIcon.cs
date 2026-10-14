using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Overlay.App;

/// <summary>Notification-area icon: shows the current status and offers the main toggles.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem enabledItem, layoutItem;
    private readonly Icon image = DrawIcon();

    public event Action? OpenSettings;
    public event Action? ToggleEnabled;
    public event Action? ToggleLayout;
    public event Action? Exit;

    public TrayIcon()
    {
        enabledItem = new Forms.ToolStripMenuItem("Overlay enabled", null, (_, _) => ToggleEnabled?.Invoke());
        layoutItem = new Forms.ToolStripMenuItem("Layout mode", null, (_, _) => ToggleLayout?.Invoke());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings?.Invoke()) { Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(enabledItem);
        menu.Items.Add(layoutItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => Exit?.Invoke()));

        icon = new Forms.NotifyIcon { Icon = image, Text = "osu! timeline overlay", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenSettings?.Invoke(); };
    }

    public void Update(string status, bool enabled, bool layoutMode)
    {
        // The tooltip is limited to 127 characters.
        string text = $"osu! timeline overlay: {status}";
        icon.Text = text.Length > 127 ? text[..124] + "..." : text;
        enabledItem.Checked = enabled;
        layoutItem.Checked = layoutMode;
    }

    /// <summary>A small timeline glyph: a circle on a bar with the "now" line, in the overlay's colours.</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bar = new SolidBrush(Color.FromArgb(70, 130, 255));
            g.FillRectangle(bar, 2, 13, 28, 6);
            using var circle = new SolidBrush(Color.FromArgb(255, 102, 170));
            g.FillEllipse(circle, 4, 6, 20, 20);
            using var edge = new Pen(Color.White, 2.5f);
            g.DrawEllipse(edge, 4, 6, 20, 20);
            using var now = new Pen(Color.FromArgb(255, 204, 34), 3);
            g.DrawLine(now, 26, 1, 26, 31);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        image.Dispose();
    }
}
