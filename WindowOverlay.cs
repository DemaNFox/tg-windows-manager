using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class WindowOverlay : Form
    {
        private readonly string _label;
        private readonly Size _overlaySize = new Size(72, 72);
        private readonly System.Action? _onClick;

        private WindowOverlay(string label, Rectangle targetBounds, System.Action? onClick)
        {
            _label = label;
            _onClick = onClick;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;

            BackColor = Color.Black;
            Opacity = 0.65;

            Size = _overlaySize;
            UpdatePosition(targetBounds);
        }

        public static WindowOverlay Create(Rectangle targetBounds, string label, System.IntPtr targetWindow, System.Action? onClick)
        {
            var overlay = new WindowOverlay(label, targetBounds, onClick);
            overlay.CreateControl();

            // –ü–æ–∫–∞–∑—ã–≤–∞–µ–º –±–µ–∑ –∞–∫—Ç–∏–≤–∞—Ü–∏–∏, —á—Ç–æ–±—ã –Ω–µ –∑–∞–∫—Ä—ã–≤–∞—Ç—å –∫–æ–Ω—Ç–µ–∫—Å—Ç–Ω–æ–µ –º–µ–Ω—é
            NativeMethods.ShowWindow(overlay.Handle, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.SetWindowPos(
                overlay.Handle,
                NativeMethods.HWND_TOPMOST,
                overlay.Left,
                overlay.Top,
                overlay.Width,
                overlay.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            // œÓÔÓ·ÛÂÏ ÔË‚ˇÁ‡Ú¸ Í ÓÍÌÛ-ˆÂÎË Ë ÔÂÂÌÂÒÚË Ì‡ Â„Ó ‡·Ó˜ËÈ ÒÚÓÎ.
            if (targetWindow != System.IntPtr.Zero)
            {
                NativeMethods.TrySetWindowOwner(overlay.Handle, targetWindow);
                if (NativeMethods.TryGetWindowDesktopId(targetWindow, out var desktopId))
                {
                    NativeMethods.TryMoveWindowToDesktop(overlay.Handle, desktopId);
                }
            }

            if (onClick != null)
            {
                overlay.Cursor = Cursors.Hand;
            }
            return overlay;
        }

        public void UpdatePosition(Rectangle targetBounds)
        {
            Location = new Point(
                targetBounds.Left + (targetBounds.Width - _overlaySize.Width) / 2,
                targetBounds.Top + (targetBounds.Height - _overlaySize.Height) / 2);
        }

        protected override bool ShowWithoutActivation => false;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_LBUTTONUP = 0x0202;
            base.WndProc(ref m);
            if (m.Msg == WM_LBUTTONUP)
            {
                _onClick?.Invoke();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            using var background = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            using var border = new Pen(Color.FromArgb(230, 255, 204, 0), 3);
            using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 204, 0)); // —è—Ä–∫–æ-–∂–µ–ª—Ç—ã–π
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var font = new Font(SystemFonts.DefaultFont.FontFamily, 24, FontStyle.Bold);

            e.Graphics.FillEllipse(background, rect);
            e.Graphics.DrawEllipse(border, rect);
            e.Graphics.DrawString(_label, font, textBrush, rect, format);
        }
    }
}


