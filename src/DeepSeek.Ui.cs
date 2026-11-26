// ============================================================================
//  DeepSeek 界面层（毛玻璃版）
//  由 PowerShell 通过 Add-Type 编译加载，只负责绘制与交互外观，不含业务逻辑。
//  注意：必须兼容 C# 5（Windows PowerShell 5.1 内置编译器）。
//    - 不用 $"" 字符串内插、?. 、=> 方法体、nameof、表达式属性初始化
// ============================================================================
using System;
using System.Collections.Generic;
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
        // 取样图：提示框里的按钮设了 GlassSource，就取提示框自己的磨砂面；
        // 否则取主窗口背景 —— 提示框里若沿用主窗口背景，同样的坐标对上的是另一块无关画面，按钮会透出不相干的颜色。
        public static void PaintGlass(Graphics g, Control c, Rectangle rc, int alpha, int radius, bool border)
        {
            if (rc.Width <= 1 || rc.Height <= 1) { return; }
            Bitmap src = Blur;
            GlassButton gb = c as GlassButton;
            if (gb != null && gb.GlassSource != null) { src = gb.GlassSource; }
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
                if (src != null)
                {
                    Point p = Geo.AbsPoint(c);
                    // 取样越界要裁剪，否则窗口变大时右侧/下侧会留下未绘制的暗带
                    int sx = Math.Max(0, p.X);
                    int sy = Math.Max(0, p.Y);
                    int ex = Math.Min(src.Width, p.X + rc.Width);
                    int ey = Math.Min(src.Height, p.Y + rc.Height);
                    if (ex > sx && ey > sy)
                    {
                        g.DrawImage(src,
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
        // 画玻璃时的取样图。提示框里的按钮指向提示框自己的磨砂面；为空表示取主窗口背景。
        public Bitmap GlassSource;
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

    // ---------------------------------------------------------------- 提示框
    // 界面里的所有提示都走这里，不再用系统 MessageBox，也不让 WinForms 的异常对话框冒出来。
    // 背景是趁窗口还没显示时，把屏幕上这一块截下来模糊后铺底，和主窗口的玻璃面板一个观感。

    public enum MsgKind
    {
        Info = 0,
        Success = 1,
        Warning = 2,
        Error = 3,
        Question = 4
    }

    public class GlassDialog : Form
    {
        private MsgKind kind;
        private string body;
        private Bitmap back;
        private Bitmap blur;
        private Bitmap frost;   // 窗口底面（模糊背景 + 雾面），按钮也从这里取样
        private Font fTitle;
        private Font fBody;
        private int result = -1;
        private int defaultIndex = 0;
        private int cancelIndex = -1;
        private List<GlassButton> buttons = new List<GlassButton>();
        private int radius = 16;
        private int W, H, pad, badge, bodyX, bodyY, bodyW, bodyH;
        private const int TopPad96 = 26;   // 以下为 96dpi 逻辑值，构造时按缩放换算
        private const int BottomPad96 = 24;
        private const int BtnGap96 = 10;

        private GlassDialog()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Color.FromArgb(255, 248, 251, 255);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW：无边框窗口的一圈柔和投影
                return cp;
            }
        }

        // captions 与 kinds 一一对应；回车触发 defaultIndex，Esc 触发 cancelIndex（-1 = 直接关闭）。
        // 返回被点中的按钮序号；提示框自己出问题时按「没同意」处理，返回 cancelIndex 或 -1。
        public static int Show(IWin32Window owner, MsgKind kind, string title, string text,
                               string[] captions, int[] kinds, int defaultIndex, int cancelIndex)
        {
            if (captions == null || captions.Length == 0) { captions = new string[] { "确定" }; }
            int r = cancelIndex;
            using (GlassDialog d = new GlassDialog())
            {
                d.kind = kind;
                d.Text = (title == null) ? string.Empty : title;   // 窗口标题：Alt+Tab、任务管理器里能看到
                d.body = (text == null) ? string.Empty : text;
                d.cancelIndex = cancelIndex;
                for (int i = 0; i < captions.Length; i++)
                {
                    GlassButton b = new GlassButton();
                    b.Text = captions[i] ?? string.Empty;
                    if (kinds != null && i < kinds.Length) { b.Kind = (ButtonKind)kinds[i]; }
                    int idx = i;
                    b.Click += delegate(object s, EventArgs e) { d.Press(idx); };
                    d.buttons.Add(b);
                }
                d.defaultIndex = defaultIndex;
                if (d.defaultIndex < 0 || d.defaultIndex >= d.buttons.Count) { d.defaultIndex = 0; }
                try
                {
                    d.Build(owner);
                    d.ShowDialog(owner);
                    r = d.result;
                }
                catch
                {
                    // 连提示框都建不出来：退回系统对话框告诉用户，但结果仍按「没同意」算
                    try { MessageBox.Show(d.body, d.Text, MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
                }
            }
            return r;
        }

        private void Press(int idx)
        {
            if (idx < 0 || idx >= buttons.Count) { return; }
            result = idx;
            Close();
        }

        private void Build(IWin32Window owner)
        {
            pad = DpiHelper.S(28);
            badge = DpiHelper.S(42);
            radius = DpiHelper.S(16);
            W = DpiHelper.S(560);
            fTitle = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold, GraphicsUnit.Point);
            fBody = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

            int btnH = DpiHelper.S(38);
            int btnGap = DpiHelper.S(BtnGap96);

            // 正文按宽度折行；太长就截断加省略号，完整信息在日志里
            bodyX = pad + badge + DpiHelper.S(16);
            bodyY = DpiHelper.S(TopPad96) + badge + DpiHelper.S(14);
            bodyW = W - bodyX - pad;
            int maxBody = DpiHelper.S(236);
            Size ts = TextRenderer.MeasureText(body, fBody, new Size(bodyW, maxBody), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            bodyH = Math.Min(maxBody, Math.Max(DpiHelper.S(22), ts.Height));

            int[] widths = new int[buttons.Count];
            int total = 0;
            for (int i = 0; i < buttons.Count; i++)
            {
                Size bs = TextRenderer.MeasureText(buttons[i].Text, buttons[i].Font);
                widths[i] = Math.Max(DpiHelper.S(92), bs.Width + DpiHelper.S(44));
                total += widths[i];
                if (i > 0) { total += btnGap; }
            }
            int btnY = bodyY + bodyH + DpiHelper.S(24);
            H = btnY + btnH + DpiHelper.S(BottomPad96);
            int x = W - pad - total;
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].Location = new Point(x, btnY);
                buttons[i].Size = new Size(widths[i], btnH);
                Controls.Add(buttons[i]);
                x += widths[i] + btnGap;
            }
            Size = new Size(W, H);

            // 居中盖在主窗口上；主窗口不在就用主屏工作区
            Rectangle area;
            Form of = owner as Form;
            if (of != null && !of.IsDisposed && of.IsHandleCreated)
            {
                area = of.RectangleToScreen(of.ClientRectangle);
            }
            else
            {
                area = Screen.PrimaryScreen.WorkingArea;
            }
            int lx = area.X + (area.Width - W) / 2;
            int ly = area.Y + (area.Height - H) / 2;
            Rectangle wa = Screen.GetWorkingArea(new Rectangle(lx, ly, W, H));
            lx = Math.Max(wa.X, Math.Min(lx, wa.Right - W));
            ly = Math.Max(wa.Y, Math.Min(ly, wa.Bottom - H));
            Location = new Point(lx, ly);

            // 窗口此刻还不可见，截到的就是它背后真正的画面
            try
            {
                back = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(back))
                {
                    g.CopyFromScreen(Location, Point.Empty, new Size(W, H), CopyPixelOperation.SourceCopy);
                }
                blur = MakeBlur(back, W, H);
            }
            catch
            {
                if (back != null) { back.Dispose(); back = null; }
                if (blur != null) { blur.Dispose(); blur = null; }
            }

            // 磨砂面 = 模糊背景 + 白色雾面，合成一张图：窗口底面画它，按钮也从它取样，
            // 这样按钮透出来的就是它正下方那块玻璃，和主窗口里按钮嵌在卡片上的观感一致。
            frost = new Bitmap(Math.Max(1, W), Math.Max(1, H), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(frost))
            {
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                if (blur != null)
                {
                    g.DrawImage(blur, new Rectangle(0, 0, W, H), new Rectangle(0, 0, W, H), GraphicsUnit.Pixel);
                    using (SolidBrush v = new SolidBrush(Color.FromArgb(208, 255, 255, 255))) { g.FillRectangle(v, 0, 0, W, H); }
                }
                else
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(255, 226, 238, 255), Color.White, LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(lg, 0, 0, W, H);
                    }
                }
            }
            for (int i = 0; i < buttons.Count; i++) { buttons[i].GlassSource = frost; }

            // 预热：显示之前先在内存里把整块界面画一遍，首次弹出时不用等绘制代码的即时编译，
            // 避免窗口先出现、按钮晚一拍才画出来。
            try
            {
                using (Bitmap warm = new Bitmap(Math.Max(1, W), Math.Max(1, H)))
                {
                    DrawToBitmap(warm, new Rectangle(0, 0, W, H));
                }
            }
            catch { }
        }

        // 与主窗口一致的磨砂做法：先缩小再放大
        private static Bitmap MakeBlur(Bitmap src, int w, int h)
        {
            int bw = Math.Max(1, w / 13);
            int bh = Math.Max(1, h / 13);
            Bitmap small = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, bw, bh));
            }
            Bitmap b2 = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b2))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(small, new Rectangle(0, 0, w, h));
            }
            small.Dispose();
            return b2;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Geo.ApplyRoundRegion(this, radius);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (frost == null) { return; }
            Graphics g = e.Graphics;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(frost, new Rectangle(0, 0, W, H), new Rectangle(0, 0, W, H), GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int bx = pad;
            int by = DpiHelper.S(TopPad96);

            // 徽标：按提示类型着色
            Color accent = AccentColor(kind);
            using (LinearGradientBrush ab = new LinearGradientBrush(new Rectangle(bx, by, badge, badge), Lighten(accent, 22), accent, LinearGradientMode.ForwardDiagonal))
            {
                g.FillEllipse(ab, bx, by, badge, badge);
            }
            if (kind == MsgKind.Success)
            {
                // 对勾用线条画，不依赖字体里有没有这个字形
                float u = (float)badge;
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddLine(bx + u * 0.24f, by + u * 0.53f, bx + u * 0.43f, by + u * 0.72f);
                    p.AddLine(bx + u * 0.43f, by + u * 0.72f, bx + u * 0.78f, by + u * 0.32f);
                    using (Pen pen = new Pen(Color.White, Math.Max(2f, u * 0.085f)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        g.DrawPath(pen, p);
                    }
                }
            }
            else
            {
                using (Font gf = new Font("Microsoft YaHei UI", (float)(badge * 0.56) * 72f / g.DpiX, FontStyle.Bold, GraphicsUnit.Point))
                {
                    TextRenderer.DrawText(g, Glyph(kind), gf, new Rectangle(bx, by, badge, badge), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }

            // 标题
            Rectangle tr = new Rectangle(bodyX, by, W - bodyX - pad, badge);
            TextRenderer.DrawText(g, Text, fTitle, tr, Color.FromArgb(255, 0x1B, 0x24, 0x30),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // 正文
            Rectangle br = new Rectangle(bodyX, bodyY, bodyW, bodyH);
            TextRenderer.DrawText(g, body, fBody, br, Color.FromArgb(255, 0x3A, 0x46, 0x57),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // 内侧高光描边，与玻璃卡片一致
            using (GraphicsPath sp = Geo.RoundRect(new Rectangle(0, 0, W - 1, H - 1), radius))
            using (Pen pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
            {
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.DrawPath(pen, sp);
            }
        }

        private static Color AccentColor(MsgKind k)
        {
            if (k == MsgKind.Success) { return Color.FromArgb(255, 0x2E, 0x9E, 0x5B); }
            if (k == MsgKind.Warning) { return Color.FromArgb(255, 0xD9, 0x8A, 0x12); }
            if (k == MsgKind.Error) { return Color.FromArgb(255, 0xD9, 0x3A, 0x2C); }
            return Color.FromArgb(255, 0x2F, 0x6F, 0xE4);
        }

        private static Color Lighten(Color c, int amount)
        {
            return Color.FromArgb(255,
                Math.Min(255, c.R + amount),
                Math.Min(255, c.G + amount),
                Math.Min(255, c.B + amount));
        }

        private static string Glyph(MsgKind k)
        {
            if (k == MsgKind.Warning) { return "!"; }
            if (k == MsgKind.Error) { return "\u00D7"; }
            if (k == MsgKind.Question) { return "?"; }
            return "i";
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Press(defaultIndex);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (cancelIndex >= 0 && cancelIndex < buttons.Count) { Press(cancelIndex); }
                else { result = -1; Close(); }
            }
        }

        // 没点任何按钮就关掉（Alt+F4 等）：按「取消」算，绝不当成同意
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (result < 0 && cancelIndex >= 0 && cancelIndex < buttons.Count) { result = cancelIndex; }
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { buttons[defaultIndex].Focus(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                for (int i = 0; i < buttons.Count; i++) { buttons[i].GlassSource = null; }
                if (back != null) { back.Dispose(); back = null; }
                if (blur != null) { blur.Dispose(); blur = null; }
                if (frost != null) { frost.Dispose(); frost = null; }
                if (fTitle != null) { fTitle.Dispose(); fTitle = null; }
                if (fBody != null) { fBody.Dispose(); fBody = null; }
            }
            base.Dispose(disposing);
        }
    }

}
