using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PCOptimizer.Services
{
    public static class TrayService
    {
        private static NotifyIcon? _trayIcon;
        private static Icon? _icon;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static event Action? ShowBrightnessRequested;
        public static event Action? ExitRequested;

        public static void Initialize()
        {
            _icon = CreateIcon();
            _trayIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = "PC Optimizer — clique duplo: Brilho e Contraste",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Brilho e Contraste", null, (_, _) =>
                System.Windows.Application.Current.Dispatcher.Invoke(() => ShowBrightnessRequested?.Invoke()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Fechar PC Optimizer", null, (_, _) =>
                System.Windows.Application.Current.Dispatcher.Invoke(() => ExitRequested?.Invoke()));

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) =>
                System.Windows.Application.Current.Dispatcher.Invoke(() => ShowBrightnessRequested?.Invoke());
        }

        public static void ShowBalloonTip(string title, string text)
        {
            _trayIcon?.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
        }

        public static void Dispose()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _icon?.Dispose();
            _icon = null;
        }

        private static Icon CreateIcon()
        {
            using var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var brush = new SolidBrush(Color.FromArgb(255, 220, 50));
            g.FillEllipse(brush, 3, 3, 10, 10);

            using var pen = new Pen(Color.FromArgb(255, 170, 0), 1.5f);
            g.DrawLine(pen, 8, 0, 8, 2);
            g.DrawLine(pen, 8, 13, 8, 15);
            g.DrawLine(pen, 0, 8, 2, 8);
            g.DrawLine(pen, 13, 8, 15, 8);
            g.DrawLine(pen, 2, 2, 4, 4);
            g.DrawLine(pen, 12, 12, 14, 14);
            g.DrawLine(pen, 12, 2, 14, 4);
            g.DrawLine(pen, 2, 12, 4, 14);

            // GetHicon cria um HICON nao gerenciado; clonamos para um Icon
            // independente e destruimos o handle original para nao vazar.
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
    }
}
