using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Crest.Helpers;
using Crest.Models;
using Forms = System.Windows.Forms;

namespace Crest.Services
{
    /// <summary>
    /// System tray icon with a small right-click menu. The icon is drawn at runtime,
    /// so no .ico resource is needed in the project.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ToolStripMenuItem _showItem;
        private readonly Forms.ToolStripMenuItem _startupItem;
        private readonly AppSettings _settings;
        private readonly IntPtr _hIcon;
        private readonly Icon _icon;

        public event EventHandler? SettingsRequested;
        public event EventHandler<bool>? VisibilityToggled;
        public event EventHandler? ExitRequested;

        public TrayIconService(AppSettings settings)
        {
            _settings = settings;
            (_icon, _hIcon) = CreateIcon();

            var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };

            var settingsItem = new Forms.ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
            settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);

            _showItem = new Forms.ToolStripMenuItem("Show notch") { Checked = true, CheckOnClick = true };
            _showItem.CheckedChanged += (_, _) => VisibilityToggled?.Invoke(this, _showItem.Checked);

            _startupItem = new Forms.ToolStripMenuItem("Start with Windows") { Checked = settings.StartWithWindows, CheckOnClick = true };
            _startupItem.CheckedChanged += (_, _) => _settings.StartWithWindows = _startupItem.Checked;
            _settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.StartWithWindows))
                    _startupItem.Checked = _settings.StartWithWindows;
            };

            var exitItem = new Forms.ToolStripMenuItem("Exit Crest", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

            menu.Items.Add(settingsItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_showItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = _icon,
                Text = "Crest",
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
            };
        }

        public void SetNotchVisible(bool visible)
        {
            if (_showItem.Checked != visible) _showItem.Checked = visible;
        }

        /// <summary>Draws a tiny black notch with three white bars.</summary>
        private static (Icon icon, IntPtr handle) CreateIcon()
        {
            const int size = 32;
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                var rect = new RectangleF(1.5f, 7.5f, size - 3f, 17f);
                using var path = RoundedRect(rect, 8.5f);
                using var fill = new SolidBrush(Color.Black);
                using var border = new Pen(Color.FromArgb(220, 255, 255, 255), 1.6f);
                g.FillPath(fill, path);
                g.DrawPath(border, path);

                using var bar = new SolidBrush(Color.White);
                float[] heights = { 6f, 11f, 8f };
                float x = 11f;
                foreach (float h in heights)
                {
                    g.FillRectangle(bar, x, 16f - h / 2f, 2.4f, h);
                    x += 4f;
                }
            }

            IntPtr hIcon = bmp.GetHicon();
            return (Icon.FromHandle(hIcon), hIcon);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _icon.Dispose();
            NativeMethods.DestroyIcon(_hIcon);
        }
    }
}
