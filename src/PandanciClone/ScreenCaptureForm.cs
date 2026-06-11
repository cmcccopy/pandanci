using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PandanciClone
{
    internal sealed class ScreenCaptureForm : Form
    {
        private readonly Bitmap _screenBitmap;
        private readonly Rectangle _virtualBounds;
        private Point _startPoint;
        private Point _currentPoint;
        private bool _selecting;
        private Rectangle _selection;

        public ScreenCaptureForm(Bitmap screenBitmap, Rectangle virtualBounds)
        {
            if (screenBitmap == null) throw new ArgumentNullException("screenBitmap");
            _screenBitmap = screenBitmap;
            _virtualBounds = virtualBounds;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = virtualBounds;
            TopMost = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            BackColor = Color.Black;

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            KeyDown += OnKeyDown;
        }

        public Bitmap TakeSelectedBitmap()
        {
            if (_selection.Width < 3 || _selection.Height < 3) return null;
            Rectangle source = Rectangle.Intersect(ViewToBitmapRect(_selection), new Rectangle(Point.Empty, _screenBitmap.Size));
            if (source.Width < 3 || source.Height < 3) return null;

            Bitmap crop = new Bitmap(source.Width, source.Height);
            using (Graphics g = Graphics.FromImage(crop))
            {
                g.DrawImage(_screenBitmap, new Rectangle(0, 0, source.Width, source.Height), source, GraphicsUnit.Pixel);
            }
            return crop;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_screenBitmap, ClientRectangle);

            using (Brush shade = new SolidBrush(Color.FromArgb(115, 0, 0, 0)))
            {
                e.Graphics.FillRectangle(shade, ClientRectangle);
            }

            Rectangle rect = GetSelectionRect();
            if (rect.Width > 0 && rect.Height > 0)
            {
                e.Graphics.DrawImage(_screenBitmap, rect, ViewToBitmapRect(rect), GraphicsUnit.Pixel);
                using (Pen pen = new Pen(Color.FromArgb(77, 135, 245), 2F))
                using (Brush fill = new SolidBrush(Color.FromArgb(35, 77, 135, 245)))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillRectangle(fill, rect);
                    e.Graphics.DrawRectangle(pen, rect);
                }

                Rectangle bitmapRect = ViewToBitmapRect(rect);
                string sizeText = bitmapRect.Width + " x " + bitmapRect.Height;
                using (Font font = new Font("Microsoft YaHei UI", 9F))
                using (Brush brush = new SolidBrush(Color.White))
                using (Brush bg = new SolidBrush(Color.FromArgb(165, 0, 0, 0)))
                {
                    SizeF size = e.Graphics.MeasureString(sizeText, font);
                    RectangleF label = new RectangleF(rect.Left, Math.Max(0, rect.Top - size.Height - 8), size.Width + 12, size.Height + 6);
                    e.Graphics.FillRectangle(bg, label);
                    e.Graphics.DrawString(sizeText, font, brush, label.Left + 6, label.Top + 3);
                }
            }
            else
            {
                string hint = "按住左键框选截图翻译区域，Esc 取消";
                using (Font font = new Font("Microsoft YaHei UI", 14F))
                using (Brush brush = new SolidBrush(Color.White))
                using (Brush bg = new SolidBrush(Color.FromArgb(135, 0, 0, 0)))
                {
                    SizeF size = e.Graphics.MeasureString(hint, font);
                    RectangleF label = new RectangleF(24, 24, size.Width + 22, size.Height + 14);
                    e.Graphics.FillRectangle(bg, label);
                    e.Graphics.DrawString(hint, font, brush, label.Left + 11, label.Top + 7);
                }
            }
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _selecting = true;
            _startPoint = e.Location;
            _currentPoint = e.Location;
            _selection = Rectangle.Empty;
            Invalidate();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_selecting) return;
            _currentPoint = e.Location;
            _selection = GetSelectionRect();
            Invalidate();
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (!_selecting || e.Button != MouseButtons.Left) return;
            _selecting = false;
            _currentPoint = e.Location;
            _selection = GetSelectionRect();
            if (_selection.Width >= 3 && _selection.Height >= 3)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                _selection = Rectangle.Empty;
                Invalidate();
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        private Rectangle GetSelectionRect()
        {
            int left = Math.Min(_startPoint.X, _currentPoint.X);
            int top = Math.Min(_startPoint.Y, _currentPoint.Y);
            int right = Math.Max(_startPoint.X, _currentPoint.X);
            int bottom = Math.Max(_startPoint.Y, _currentPoint.Y);
            Rectangle rect = Rectangle.FromLTRB(left, top, right, bottom);
            return Rectangle.Intersect(rect, ClientRectangle);
        }

        private Rectangle ViewToBitmapRect(Rectangle viewRect)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return Rectangle.Empty;

            float scaleX = _screenBitmap.Width / (float)ClientSize.Width;
            float scaleY = _screenBitmap.Height / (float)ClientSize.Height;
            int left = (int)Math.Floor(viewRect.Left * scaleX);
            int top = (int)Math.Floor(viewRect.Top * scaleY);
            int right = (int)Math.Ceiling(viewRect.Right * scaleX);
            int bottom = (int)Math.Ceiling(viewRect.Bottom * scaleY);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
    }
}
