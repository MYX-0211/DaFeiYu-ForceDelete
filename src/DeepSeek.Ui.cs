// ============================================================================
//  DeepSeek 界面层（毛玻璃版）
//  由 PowerShell 通过 Add-Type 编译加载，只负责绘制与交互外观，不含业务逻辑。
//  注意：必须兼容 C# 5（Windows PowerShell 5.1 内置编译器）。
//    - 不用 $"" 字符串内插、?. 、=> 方法体、nameof、表达式属性初始化
// ============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeepSeek
{
    // ---------------------------------------------------------------- 工具

    public static class DpiHelper
    {
        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hDC, int nIndex);

        public static float K = 1.0f;

        // 必须在创建任何窗口 / Graphics 对象之前调用
        public static void Enable()
        {
            int hr = -1;
            try { hr = SetProcessDpiAwareness(2); }
            catch { hr = -1; }
            if (hr != 0)
            {
                try { SetProcessDPIAware(); }
                catch { }
            }
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int px = GetDeviceCaps(dc, 88); // LOGPIXELSX
                ReleaseDC(IntPtr.Zero, dc);
                if (px > 0)
                {
                    K = px / 96f;
                }
                if (K < 0.8f || K > 4f) { K = 1f; }
            }
            catch { K = 1f; }
        }

        public static int S(int v)
        {
            return (int)Math.Round((double)v * (double)K);
        }
    }

    public static class Geo
    {
        // 控件在窗体客户区中的绝对坐标。
        // 必须走 PointToScreen：父容器在 150% 缩放下可能落在 .5 物理像素上，
        // 自己累加 Left/Top 会得到分数坐标，取样时重采样 -> 标签四周出现 1px 接缝。
        public static Point AbsPoint(Control c)
        {
            Form f = c.FindForm();
            if (f != null && f.IsHandleCreated && c.IsHandleCreated)
            {
                Point s = c.PointToScreen(Point.Empty);
                Point fs = f.PointToScreen(Point.Empty);
                return new Point(s.X - fs.X, s.Y - fs.Y);
            }
            int x = 0;
            int y = 0;
            Control p = c;
            while (p != null && !(p is Form))
            {
                x += p.Left;
                y += p.Top;
                p = p.Parent;
            }
            return new Point(x, y);
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) { d = r.Width; }
            if (d > r.Height) { d = r.Height; }
            if (d < 2)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        // 把窗口裁成圆角：控件自身区域是矩形，不裁的话四角会露出底色
        public static void ApplyRoundRegion(Control c, int radius)
        {
            if (c.Width < 2 || c.Height < 2) { return; }
            try
            {
                using (GraphicsPath p = RoundRect(new Rectangle(0, 0, c.Width, c.Height), radius))
                {
                    Region old = c.Region;
                    c.Region = new Region(p);
                    if (old != null) { old.Dispose(); }
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- 场景

    public static class Scene
    {
        public static Bitmap Source;   // 用户提供的原图
        public static Bitmap Sharp;    // 铺满窗口的背景
        public static Bitmap Blur;     // 同尺寸模糊版，毛玻璃取样用
        public static int CardTint = 94; // 玻璃层白色不透明度

        public static void Build(int w, int h)
        {
            if (w <= 0 || h <= 0) { return; }

            Bitmap sharp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(sharp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // 兜底底色：图片缺失时也不至于一片白
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new Rectangle(0, 0, w, h),
                    Color.FromArgb(255, 226, 238, 255),
                    Color.FromArgb(255, 255, 255, 255),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(lg, 0, 0, w, h);
                }

                if (Source != null && Source.Width > 0 && Source.Height > 0)
                {
                    // cover 布局：铺满、居中裁切
                    float scale = Math.Max((float)w / (float)Source.Width, (float)h / (float)Source.Height);
                    int dw = (int)Math.Ceiling((double)Source.Width * (double)scale);
                    int dh = (int)Math.Ceiling((double)Source.Height * (double)scale);
                    int dx = (w - dw) / 2;
                    int dy = (h - dh) / 2;
                    g.DrawImage(Source, new Rectangle(dx, dy, dw, dh));
                }

                // 轻微提亮，保证深色文字的对比度
                using (SolidBrush veil = new SolidBrush(Color.FromArgb(16, 255, 255, 255)))
                {
                    g.FillRectangle(veil, 0, 0, w, h);
                }
            }

            // 模糊版：先缩小再放大，得到柔和的磨砂取样
            Bitmap blur = null;
            try
            {
                int bw = Math.Max(1, w / 13);
                int bh = Math.Max(1, h / 13);
                Bitmap small = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(sharp, new Rectangle(0, 0, bw, bh));
                }
                blur = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(blur))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, new Rectangle(0, 0, w, h));
                }
                small.Dispose();
            }
            catch
            {
                if (blur != null) { blur.Dispose(); }
                blur = new Bitmap(sharp);
            }

            if (Sharp != null) { Sharp.Dispose(); }
            if (Blur != null) { Blur.Dispose(); }
            Sharp = sharp;
            Blur = blur;
        }

        // 所有玻璃表面共用：取模糊背景 + 白色雾面 + 高光描边
        public static void PaintGlass(Graphics g, Control c, Rectangle rc, int alpha, int radius, bool border)
        {
            if (rc.Width <= 1 || rc.Height <= 1) { return; }
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 关键：HighQuality 会给图像加 0.5px 偏移，1:1 取样时整块会被重采样，
            // 与父卡片的像素差 1px -> 标签四周出现细缝。这里必须用 None。
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            // 填充必须覆盖整个客户区，否则边缘会露出 1px 未绘制的暗线
            using (GraphicsPath fillPath = Geo.RoundRect(new Rectangle(0, 0, rc.Width, rc.Height), radius))
            {
                g.SetClip(fillPath);
                if (Blur != null)
                {
                    Point p = Geo.AbsPoint(c);
                    // 取样越界要裁剪，否则窗口变大时右侧/下侧会留下未绘制的暗带
                    int sx = Math.Max(0, p.X);
                    int sy = Math.Max(0, p.Y);
                    int ex = Math.Min(Blur.Width, p.X + rc.Width);
                    int ey = Math.Min(Blur.Height, p.Y + rc.Height);
                    if (ex > sx && ey > sy)
                    {
                        g.DrawImage(Blur,
                            new Rectangle(sx - p.X, sy - p.Y, ex - sx, ey - sy),
                            new Rectangle(sx, sy, ex - sx, ey - sy),
                            GraphicsUnit.Pixel);
                    }
                }
                if (alpha > 0)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    {
                        g.FillRectangle(b, rc);
                    }
                }
                g.ResetClip();
            }
            if (border)
            {
                // 描边单独走一条内缩 1px 的路径，保证线条完整落在客户区内
                using (GraphicsPath strokePath = Geo.RoundRect(new Rectangle(0, 0, rc.Width - 1, rc.Height - 1), radius))
                using (Pen pen = new Pen(Color.FromArgb(170, 255, 255, 255), 1f))
                {
                    g.DrawPath(pen, strokePath);
                }
            }
            g.Restore(st);
        }
    }

    // ---------------------------------------------------------------- 基类

    public class RoundedPanel : Panel
    {
        public int Radius = 16;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(255, 248, 251, 255);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Radius > 0) { Geo.ApplyRoundRegion(this, Radius); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Radius > 0) { Geo.ApplyRoundRegion(this, Radius); }
        }
    }

    public class RoundedControl : Control
    {
        public int Radius = 10;

        public RoundedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(255, 248, 251, 255);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Radius > 0) { Geo.ApplyRoundRegion(this, Radius); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Radius > 0) { Geo.ApplyRoundRegion(this, Radius); }
        }
    }

    // ---------------------------------------------------------------- 控件

    public class SceneForm : Form
    {
        public SceneForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(255, 240, 246, 255);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Win11：圆角窗口 + 标题栏配色（旧系统上静默失败）
            try
            {
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch { }
            try
            {
                int caption = 0x00FFF6EE; // COLORREF 0x00BBGGRR -> #EEF6FF
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
            }
            catch { }
            try
            {
                int text = 0x00201810;    // #101820
                DwmSetWindowAttribute(Handle, 36, ref text, 4);
            }
            catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0) { return; }
            if (Scene.Sharp == null)
            {
                e.Graphics.Clear(BackColor);
                return;
            }
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            e.Graphics.DrawImage(Scene.Sharp,
                new Rectangle(0, 0, w, h),
                new Rectangle(0, 0, Scene.Sharp.Width, Scene.Sharp.Height),
                GraphicsUnit.Pixel);
        }
    }

    public class GlassCard : RoundedPanel
    {
        public int TintAlpha = -1;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            int a = TintAlpha < 0 ? Scene.CardTint : TintAlpha;
            Scene.PaintGlass(e.Graphics, this, ClientRectangle, a, Radius, true);
        }
    }

    public class GlassLabel : RoundedControl
    {
        public int TintAlpha = -1;   // <0 = 与卡片一致
        public int Align = 0;        // 0 左 1 中 2 右
        public bool WordWrap = false;
        public bool NoGlass = false; // 直接画在卡片上时不重复取样

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (NoGlass)
            {
                // 不取样背景：用 BackColor 实心铺满（只用于叠在实心面板上的提示文字）。
                // 注意不能"什么都不画"——AllPaintingInWmPaint 下子窗口区域不会被父窗口内容填充，会留黑块。
                using (SolidBrush b = new SolidBrush(BackColor)) { e.Graphics.FillRectangle(b, ClientRectangle); }
                return;
            }
            int a = TintAlpha < 0 ? Scene.CardTint : TintAlpha;
            Scene.PaintGlass(e.Graphics, this, ClientRectangle, a, Radius, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(Text)) { return; }
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            if (Align == 1) { flags |= TextFormatFlags.HorizontalCenter; }
            else if (Align == 2) { flags |= TextFormatFlags.Right; }
            else { flags |= TextFormatFlags.Left; }
            if (WordWrap)
            {
                flags |= TextFormatFlags.WordBreak;
                flags = flags & ~TextFormatFlags.VerticalCenter;
            }
            else
            {
                flags |= TextFormatFlags.SingleLine;
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
        }
    }

    public enum ButtonKind
    {
        Default = 0,
        Primary = 1,
        Danger = 2,
        Ghost = 3
    }

    public class GlassButton : RoundedControl
    {
        public ButtonKind Kind = ButtonKind.Default;
        private bool hover;
        private bool press;

        public GlassButton()
        {
            Cursor = Cursors.Hand;
            Radius = 10;
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            ForeColor = Color.FromArgb(255, 0x1B, 0x24, 0x30);
        }

        public static GlassButton Make(string text, int kind, int x, int y, int w, int h)
        {
            GlassButton b = new GlassButton();
            b.Text = text;
            b.Kind = (ButtonKind)kind;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            return b;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            press = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            press = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            press = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle rc = new Rectangle(0, 0, Width, Height);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Color fore = Color.FromArgb(255, 0x1B, 0x24, 0x30);

            if (Kind == ButtonKind.Primary)
            {
                fore = Color.White;
                using (GraphicsPath path = Geo.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
                {
                    Color c1 = press ? Color.FromArgb(255, 0xC9, 0x2F, 0x22) : Color.FromArgb(255, 0xE8, 0x4A, 0x39);
                    Color c2 = press ? Color.FromArgb(255, 0xB0, 0x25, 0x1A) : Color.FromArgb(255, 0xC9, 0x2F, 0x22);
                    using (LinearGradientBrush br = new LinearGradientBrush(rc, c1, c2, LinearGradientMode.Vertical))
                    {
                        g.FillPath(br, path);
                    }
                }
            }
            else if (Kind == ButtonKind.Ghost)
            {
                fore = hover ? Color.FromArgb(255, 0x1B, 0x24, 0x30) : Color.FromArgb(255, 0x6B, 0x7A, 0x8F);
                Scene.PaintGlass(g, this, rc, hover ? 80 : 0, Radius, false);
            }
            else
            {
                int alpha = press ? 190 : (hover ? 168 : 132);
                if (Kind == ButtonKind.Danger)
                {
                    fore = Color.FromArgb(255, 0xC0, 0x2A, 0x1E);
                }
                Scene.PaintGlass(g, this, rc, alpha, Radius, false);
                using (GraphicsPath path = Geo.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
                {
                    if (Kind == ButtonKind.Danger)
                    {
                        using (Pen pen = new Pen(Color.FromArgb(hover ? 220 : 160, 0xD9, 0x30, 0x25), 1f))
                        {
                            g.DrawPath(pen, path);
                        }
                    }
                    else
                    {
                        using (Pen pen = new Pen(Color.FromArgb(hover ? 235 : 175, 196, 212, 234), 1f))
                        {
                            g.DrawPath(pen, path);
                        }
                    }
                }
            }

            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Text, Font, rc, fore, flags);
        }
    }

    public class ToggleChip : RoundedControl
    {
        public bool Checked = true;
        private bool hover;

        public ToggleChip()
        {
            Cursor = Cursors.Hand;
            Radius = 15;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            ForeColor = Color.FromArgb(255, 0x1B, 0x24, 0x30);
        }

        public static ToggleChip Make(string text, bool isChecked, int x, int y)
        {
            ToggleChip c = new ToggleChip();
            c.Text = text;
            c.Checked = isChecked;
            c.Location = new Point(x, y);
            Size ts = TextRenderer.MeasureText(text, c.Font);
            c.Size = new Size(ts.Width + 46, 30);
            return c;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle rc = new Rectangle(0, 0, Width, Height);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int alpha = Checked ? (hover ? 190 : 165) : (hover ? 130 : 104);
            Scene.PaintGlass(g, this, rc, alpha, Radius, true);

            Color dot = Checked ? Color.FromArgb(255, 0xD9, 0x30, 0x25) : Color.FromArgb(255, 0xC2, 0xCE, 0xDE);
            if (Checked)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 0xD9, 0x30, 0x25)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath p = Geo.RoundRect(new Rectangle(6, Height / 2 - 10, Height - 20, Height - 20), (Height - 20) / 2))
                    {
                        g.FillPath(b, p);
                    }
                }
            }

            using (SolidBrush db = new SolidBrush(dot))
            {
                g.FillEllipse(db, 11, Height / 2 - 3, 7, 7);
            }

            Color fore = Checked ? Color.FromArgb(255, 0x8E, 0x1D, 0x14) : Color.FromArgb(255, 0x3A, 0x46, 0x57);
            Rectangle tr = new Rectangle(25, 0, Width - 25, Height);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Text, Font, tr, fore, flags);
        }
    }

    // 内嵌的浅色“井”：列表、日志的阅读面
    public class WellPanel : RoundedPanel
    {
        public Color FillColor = Color.FromArgb(255, 247, 250, 254);
        public Color BorderColor = Color.FromArgb(255, 224, 233, 246);

        public WellPanel()
        {
            Radius = 12;
            BackColor = Color.FromArgb(255, 247, 250, 254);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle rc = new Rectangle(0, 0, Width, Height);
            if (rc.Width <= 1 || rc.Height <= 1) { return; }
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (GraphicsPath p = Geo.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            {
                using (SolidBrush b = new SolidBrush(FillColor)) { g.FillPath(b, p); }
                using (Pen pen = new Pen(BorderColor, 1f)) { g.DrawPath(pen, p); }
            }
        }
    }

    // 半透明“井”：给待删除列表用，背景是玻璃而不是实心色，
    // 并对原生 ListBox 返回 NULL_BRUSH，让它不再擦除背景、直接透出下面的玻璃。
    public class GlassWellPanel : WellPanel
    {
        public int TintAlpha = 58;

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int nIndex);

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle rc = new Rectangle(0, 0, Width, Height);
            if (rc.Width <= 1 || rc.Height <= 1) { return; }
            Scene.PaintGlass(e.Graphics, this, rc, TintAlpha, Radius, true);
        }

        protected override void WndProc(ref Message m)
        {
            // WM_CTLCOLORLISTBOX = 0x0134：返回 NULL_BRUSH，原生 ListBox 就不再擦除背景
            if (m.Msg == 0x0134)
            {
                m.Result = GetStockObject(5);
                return;
            }
            base.WndProc(ref m);
        }
    }

    // 列表本身不擦除背景，行底色由 DrawItem 用半透明色自己画
    public class GlassListBox : ListBox
    {
        public int TintAlpha = 72;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014)
            {
                // WM_ERASEBKGND：自己把玻璃画上去，替代原生控件的白色底
                using (Graphics g = Graphics.FromHdc(m.WParam))
                {
                    Rectangle rc = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                    if (rc.Width > 1 && rc.Height > 1)
                    {
                        Scene.PaintGlass(g, this, rc, TintAlpha, 16, true);
                    }
                }
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }
    }

    public class GlassProgress : RoundedControl
    {
        private int _value;
        public int Maximum = 100;

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < 0) { v = 0; }
                if (v > Maximum) { v = Maximum; }
                _value = v;
                Invalidate();
            }
        }

        public GlassProgress()
        {
            Radius = 4;
            BackColor = Color.FromArgb(255, 248, 251, 255);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle rc = new Rectangle(0, 0, Width, Height);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int rr = Math.Max(2, Height / 2);
            using (GraphicsPath p = Geo.RoundRect(rc, rr))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                {
                    g.FillPath(b, p);
                }
            }
            if (Maximum > 0 && _value > 0)
            {
                int w = (int)((double)Width * ((double)_value / (double)Maximum));
                if (w > 1)
                {
                    using (GraphicsPath p = Geo.RoundRect(new Rectangle(0, 0, w, Height), rr))
                    {
                        Color c1 = Color.FromArgb(255, 0xE8, 0x4A, 0x39);
                        Color c2 = Color.FromArgb(255, 0xC9, 0x2F, 0x22);
                        using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, w, Height), c1, c2, LinearGradientMode.Horizontal))
                        {
                            g.FillPath(br, p);
                        }
                    }
                }
            }
        }
    }

}
