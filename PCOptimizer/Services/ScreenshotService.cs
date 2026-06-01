using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using PCOptimizer.Views;

namespace PCOptimizer.Services
{
    public static class ScreenshotService
    {
        /// <summary>
        /// Shows the area-selection overlay, captures the chosen region, saves as PNG
        /// and opens Explorer on the file. Returns the saved path, or null if cancelled.
        /// Temporarily lowers ownerWindow's Topmost so the overlay can appear above it.
        /// </summary>
        public static async Task<string?> CaptureAreaAsync(Window ownerWindow)
        {
            bool wasTopmost = ownerWindow.Topmost;
            ownerWindow.Topmost = false;

            try
            {
                var overlay = new ScreenshotOverlayWindow();
                bool? result = overlay.ShowDialog();
                if (result != true || overlay.CaptureRegion is not { } region)
                    return null;

                await Task.Delay(120);

                using var bmp = new System.Drawing.Bitmap(region.Width, region.Height);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(region.Location, System.Drawing.Point.Empty, region.Size);

                string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string fileName = $"Captura_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                string path = Path.Combine(folder, fileName);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

                return path;
            }
            finally
            {
                ownerWindow.Topmost = wasTopmost;
            }
        }
    }
}
