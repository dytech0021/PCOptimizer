using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;

namespace PCOptimizer.Views
{
    public partial class ScreenshotOverlayWindow : Window
    {
        private Point _startPoint;
        private bool _isDragging;

        public System.Drawing.Rectangle? CaptureRegion { get; private set; }

        public ScreenshotOverlayWindow()
        {
            InitializeComponent();
            Left   = SystemParameters.VirtualScreenLeft;
            Top    = SystemParameters.VirtualScreenTop;
            Width  = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _startPoint = e.GetPosition(SelectionCanvas);
            _isDragging = true;
            SelectionRect.Visibility = Visibility.Visible;
            SelectionCanvas.CaptureMouse();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var cur = e.GetPosition(SelectionCanvas);
            double l = Math.Min(_startPoint.X, cur.X);
            double t = Math.Min(_startPoint.Y, cur.Y);
            double w = Math.Abs(cur.X - _startPoint.X);
            double h = Math.Abs(cur.Y - _startPoint.Y);
            System.Windows.Controls.Canvas.SetLeft(SelectionRect, l);
            System.Windows.Controls.Canvas.SetTop(SelectionRect, t);
            SelectionRect.Width  = w;
            SelectionRect.Height = h;
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            SelectionCanvas.ReleaseMouseCapture();

            var cur = e.GetPosition(SelectionCanvas);
            double l = Math.Min(_startPoint.X, cur.X);
            double t = Math.Min(_startPoint.Y, cur.Y);
            double w = Math.Abs(cur.X - _startPoint.X);
            double h = Math.Abs(cur.Y - _startPoint.Y);

            if (w > 5 && h > 5)
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                // Convert logical coords (relative to virtual screen origin) → physical pixels
                int px = (int)Math.Round((Left + l) * sx);
                int py = (int)Math.Round((Top  + t) * sy);
                int pw = (int)Math.Round(w * sx);
                int ph = (int)Math.Round(h * sy);

                CaptureRegion = new System.Drawing.Rectangle(px, py, pw, ph);
                DialogResult = true;
            }
            else
            {
                DialogResult = false;
            }
            Close();
        }
    }
}
