using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace TelegramTrayLauncher
{
    internal static class IconFactory
    {
        /// <summary>
        /// Рисует компактный и читаемый значок для трея.
        /// </summary>
        public static Icon CreateTrayIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                var outer = new Rectangle(2, 2, 28, 28);
                using var blue = new LinearGradientBrush(outer, Color.FromArgb(0, 136, 204), Color.FromArgb(0, 180, 235), LinearGradientMode.ForwardDiagonal);
                using var border = new Pen(Color.White, 2.2f);
                using var textBrush = new SolidBrush(Color.White);
                using var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);

                g.FillEllipse(blue, outer);
                g.DrawEllipse(border, outer);
                g.DrawString("TM", font, textBrush, outer, new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                });
            }

            // Превращаем Bitmap в Icon через поток
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            using var tmp = (Bitmap)Image.FromStream(ms);
            IntPtr hIcon = tmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }
    }
}
