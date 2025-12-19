using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class WindowOverlay : Form
    {
        private readonly string _label;
        private readonly Size _overlaySize = new Size(72, 72);

        private WindowOverlay(string label, Rectangle targetBounds)
        {
            _label = label;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;

            BackColor = Color.Black;
            Opacity = 0.65;

            Size = _overlaySize;
            Location = new Point(
                targetBounds.Left + (targetBounds.Width - _overlaySize.Width) / 2,
                targetBounds.Top + (targetBounds.Height - _overlaySize.Height) / 2);
        }

        public static WindowOverlay Create(Rectangle targetBounds, string label)
        {
            var overlay = new WindowOverlay(label, targetBounds);
            overlay.CreateControl();

            // Показываем без активации, чтобы не закрывать контекстное меню
            NativeMethods.ShowWindow(overlay.Handle, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.SetWindowPos(
                overlay.Handle,
                NativeMethods.HWND_TOPMOST,
                overlay.Left,
                overlay.Top,
                overlay.Width,
                overlay.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            return overlay;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                cp.ExStyle |= unchecked((int)0x08000000); // WS_EX_NOACTIVATE, чтобы не забирать фокус и не закрывать меню
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            using var background = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            using var border = new Pen(Color.FromArgb(230, 255, 204, 0), 3);
            using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 204, 0)); // ярко-желтый
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var font = new Font(SystemFonts.DefaultFont.FontFamily, 24, FontStyle.Bold);

            e.Graphics.FillEllipse(background, rect);
            e.Graphics.DrawEllipse(border, rect);
            e.Graphics.DrawString(_label, font, textBrush, rect, format);
        }
    }
}
