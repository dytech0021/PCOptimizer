using System;
using System.Drawing;
using System.Windows.Forms;

namespace PCOptimizer.Services
{
    public static class TrayService
    {
        private static NotifyIcon? _trayIcon;
        private static Icon? _icon;

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

        private const string IconResource = "PCOptimizer.Assets.icon.ico";

        /// <summary>
        /// Carrega o mesmo icone do executavel (Assets/icon.ico, embutido como
        /// recurso) no tamanho que o Windows pede para a bandeja. O .ico tem
        /// varias resolucoes (16, 20, 24, 32...), entao em tela de alta
        /// densidade o icone sai nitido em vez de ser esticado de 16px.
        /// </summary>
        private static Icon CreateIcon()
        {
            var size = SystemInformation.SmallIconSize;
            try
            {
                using var stream = typeof(TrayService).Assembly.GetManifestResourceStream(IconResource);
                if (stream != null)
                {
                    return new Icon(stream, size);
                }
            }
            catch
            {
                // cai no icone do proprio executavel logo abaixo
            }

            // Reserva: o icone carimbado no .exe pelo <ApplicationIcon>.
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exe);
                    if (extracted != null)
                    {
                        return extracted;
                    }
                }
            }
            catch
            {
                // ignora e usa o icone padrao do sistema
            }

            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
