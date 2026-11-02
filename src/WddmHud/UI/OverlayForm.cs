// =============================================================================
// WDDM HUD by pascalkeeper — Clean owner-drawn overlay
// Copyright (C) 2026 Joseph Rocco Peransi
// www.github.com/pascalkeeper
// - Only paints metrics that have real values (no N/A clutter)
// - Resize scales content from a fixed design size; top-left stays put
// - RAM temperature removed (no sensor on AW17R4)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WddmHud.Core;
using WddmHud.Hardware;

namespace WddmHud.UI
{
    public sealed class OverlayForm : Form
    {
        /// <summary>Design canvas size — all layout is authored at this resolution then scaled.</summary>
        public const int DesignWidth = 340;
        public const int DesignHeight = 220;

        private readonly HudConfig _config;
        private readonly Panel _resizeGrip;
        private System.Windows.Forms.Timer? _topmostTimer;
        private bool _inCompose;
        private bool _fitQueued;
        private bool _userSized;

        private LiveMetrics? _metrics;
        private HudConfig? _paintConfig;

        private bool _dragging;
        private Point _dragStartCursor;
        private Point _dragStartLoc;
        private bool _resizing;
        private Size _resizeStartSize;
        private Point _resizeStartCursor;

        private float _scale = 1f;
        private bool _gameRunning;
        private bool _gameClickThroughApplied;

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        private static readonly Color TextOk = Color.FromArgb(255, 0, 255, 140);

        private Color Accent
        {
            get
            {
                HudConfig cfg = _paintConfig ?? _config;
                int r = Math.Clamp(cfg.AccentR, 0, 255);
                int g = Math.Clamp(cfg.AccentG, 0, 255);
                int b = Math.Clamp(cfg.AccentB, 0, 255);
                if (r + g + b < 48)
                    return TextOk;
                return Color.FromArgb(255, r, g, b);
            }
        }

        private Color DimAccent
        {
            get
            {
                Color accent = Accent;
                return Color.FromArgb(255, accent.R * 7 / 10, accent.G * 7 / 10, accent.B * 7 / 10);
            }
        }

        public Color CurrentAccent => Accent;

        public void PreviewAccent(Color color)
        {
            _config.AccentR = color.R;
            _config.AccentG = color.G;
            _config.AccentB = color.B;
            if (_paintConfig != null && !ReferenceEquals(_paintConfig, _config))
            {
                _paintConfig.AccentR = color.R;
                _paintConfig.AccentG = color.G;
                _paintConfig.AccentB = color.B;
            }
            try
            {
                if (IsHandleCreated)
                    ComposeAndPush();
            }
            catch { /* ignore */ }
        }

        public void CommitAccent()
        {
            _config.Save();
        }

        public bool UseFahrenheit => (_paintConfig ?? _config).UseFahrenheit;

        public void ToggleFahrenheit()
        {
            bool next = !_config.UseFahrenheit;
            _config.UseFahrenheit = next;
            if (_paintConfig != null && !ReferenceEquals(_paintConfig, _config))
                _paintConfig.UseFahrenheit = next;
            _config.Save();
            try
            {
                if (IsHandleCreated)
                    ComposeAndPush();
            }
            catch { /* ignore */ }
        }

        public void OpenColorWheel(Point cursor)
        {
            if (!IsHandleCreated)
            {
                _ = Handle;
            }
            BeginInvoke(new Action(() =>
            {
                try
                {
                    ColorWheelPopup.ShowDroppedUp(this, cursor);
                }
                catch (Exception ex)
                {
                    Logger.Error("Change color failed", ex);
                }
            }));
        }
        private static readonly Color TextWarn = Color.FromArgb(255, 255, 190, 60);
        private static readonly Color TextCrit = Color.FromArgb(255, 255, 90, 90);
        private static readonly Color PanelFill = Color.FromArgb(90, 8, 12, 8);
        private static readonly Color Outline = Color.FromArgb(255, 0, 0, 0);

        private static readonly string[] GameProcessNames =
        {
            "FortniteClient-Win64-Shipping",
            "FortniteClient-Win64-Shipping_EAC_EOS",
            "FortniteLauncher",
            "RDR2",
            "PlayRDR2",
            "WRC9",
            "WRC10",
            "r5apex_dx12",
            "ApexLauncher"
        };

        public event Action? ExitRequested;
        public event Action? ToggleClickThroughRequested;

        public OverlayForm(HudConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Sanitize();
            _paintConfig = _config;
            _userSized = _config.UserSizedFrame;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ControlBox = false;
            DoubleBuffered = true;
            // Per-pixel alpha via UpdateLayeredWindow — no TransparencyKey.
            // Color-key + ClearType was blending green glyphs into magenta (purple fringes).
            BackColor = Color.Black;
            Text = "WDDM HUD by pascalkeeper";

            int w = Math.Clamp(_config.WindowWidth, 64, 900);
            int h = Math.Clamp(_config.WindowHeight, 32, 700);
            SetBounds(_config.WindowX, _config.WindowY, w, h);
            // Do not fade the whole window — that made the HUD a dark slab over the game.
            try { Opacity = 1.0; } catch { }

            try
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }
            catch { /* ignore */ }

            _resizeGrip = new Panel
            {
                Size = new Size(14, 14),
                BackColor = Color.FromArgb(0, 160, 70),
                Cursor = Cursors.SizeNWSE,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            // The corner control is not the drag surface. The invisible plate is.
            _resizeGrip.Visible = false;
            Controls.Add(_resizeGrip);
            PlaceGrip();

            MouseDown += OnDragDown;
            MouseMove += OnDragMove;
            MouseUp += OnDragUp;
            MouseClick += OnRightClick;

            _resizeGrip.MouseDown += OnResizeDown;
            _resizeGrip.MouseMove += OnResizeMove;
            _resizeGrip.MouseUp += OnResizeUp;

            ContextMenuStrip = BuildMenu();

            HandleCreated += (_, _) =>
            {
                ApplyExtendedStyles();
                ForceTopMost();
                StartTopmostTimer();
                ComposeAndPush();
            };

            Resize += (_, _) =>
            {
                if (_inCompose)
                    return;
                UpdateScale();
                PlaceGrip();
                ComposeAndPush();
            };

            UpdateScale();
            Logger.Info("OverlayForm ready (transparent topmost, design " + DesignWidth + "x" + DesignHeight + ")");
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private void UpdateScale()
        {
            if (!_userSized)
            {
                _scale = 1f;
                return;
            }

            int nw = _config.NaturalWidth > 64 ? _config.NaturalWidth : Math.Max(64, ClientSize.Width);
            int nh = _config.NaturalHeight > 32 ? _config.NaturalHeight : Math.Max(32, ClientSize.Height);
            float sx = ClientSize.Width / (float)nw;
            float sy = ClientSize.Height / (float)nh;
            _scale = Math.Clamp(Math.Min(sx, sy), 0.7f, 2.5f);
        }

        private void PlaceGrip()
        {
            _resizeGrip.Location = new Point(
                Math.Max(0, ClientSize.Width - _resizeGrip.Width - 2),
                Math.Max(0, ClientSize.Height - _resizeGrip.Height - 2));
        }

        private void StartTopmostTimer()
        {
            if (_topmostTimer != null) return;
            _topmostTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _topmostTimer.Tick += (_, _) => TickStayInFront();
            _topmostTimer.Start();
        }

        private void TickStayInFront()
        {
            if (IsDisposed || !_config.StayInFront) return;
            bool game = IsGameRunning();
            if (game != _gameRunning)
            {
                _gameRunning = game;
                Logger.Info(game ? "Game detected — HUD stay-in-front + click-through" : "Game exited — restoring HUD input");
                if (game)
                {
                    if (!Visible) Show();
                    if (_config.AutoClickThroughInGame && !_config.ClickThrough && !_gameClickThroughApplied)
                    {
                        _gameClickThroughApplied = true;
                        ApplyExtendedStyles();
                        ComposeAndPush();
                    }
                }
                else if (_gameClickThroughApplied)
                {
                    _gameClickThroughApplied = false;
                    ApplyExtendedStyles();
                    ComposeAndPush();
                }
            }

            _topmostTimer!.Interval = game ? 200 : 800;
            if (Visible)
                ForceTopMost();
        }

        private static bool IsGameRunning()
        {
            try
            {
                foreach (string name in GameProcessNames)
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    if (procs != null && procs.Length > 0)
                    {
                        foreach (var p in procs) p.Dispose();
                        return true;
                    }
                }
            }
            catch { /* ignore */ }
            return false;
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Toggle click-through", null, (_, _) => ToggleClickThroughRequested?.Invoke());
            menu.Items.Add("Save size/position", null, (_, _) => PersistGeometry());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open config folder", null, (_, _) => OpenPath(AppPaths.ConfigDirectory));
            menu.Items.Add("Open logs", null, (_, _) => OpenPath(AppPaths.LogDirectory));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Minimize to tray", null, (_, _) => Hide());
            menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
            DateTime opened = DateTime.UtcNow;
            bool allowClose = false;
            menu.Opened += (_, _) => { opened = DateTime.UtcNow; allowClose = false; };
            menu.ItemClicked += (_, _) => { allowClose = true; };
            menu.Closing += (_, e) =>
            {
                if (allowClose || e.CloseReason != ToolStripDropDownCloseReason.AppFocusChange)
                    return;
                bool justOpened = (DateTime.UtcNow - opened).TotalMilliseconds < 700;
                if (justOpened || menu.Bounds.Contains(Cursor.Position))
                    e.Cancel = true;
            };
            return menu;
        }

        private static void OpenPath(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Logger.Error("OpenPath", ex); }
        }

        public void ApplyMetrics(LiveMetrics m, HudConfig cfg)
        {
            if (IsDisposed || m == null) return;
            _metrics = m;
            _paintConfig = cfg ?? _config;
            try
            {
                if (IsHandleCreated)
                    ComposeAndPush();
            }
            catch { /* ignore */ }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Layered window: no GDI background fill (that was the magenta key).
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Content is pushed via UpdateLayeredWindow in ComposeAndPush.
        }

        private void ComposeAndPush()
        {
            if (IsDisposed || !IsHandleCreated || _inCompose) return;
            _inCompose = true;
            int w = Math.Max(1, Width);
            int h = Math.Max(1, Height);
            try
            {
                using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    PaintMetrics(g);
                }
                PushLayered(bmp);
            }
            catch (Exception ex)
            {
                Logger.Error("ComposeAndPush", ex);
            }
            finally
            {
                _inCompose = false;
            }
        }

        private void PaintMetrics(Graphics g)
        {
            var m = _metrics;
            var cfg = _paintConfig ?? _config;
            UpdateScale();
            float s = _scale;
            // Alpha 1 is invisible on the desktop and still a drag target.
            // A fully clear pixel falls through a layered window.
            if (!EffectiveClickThrough)
            {
                using var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
                g.FillRectangle(hit, 0, 0, Width, Height);
                DrawResizeCorner(g);
            }

            if (m == null)
            {
                DrawLine(g, "●  starting…", 10, 10, Accent, 11f * s, FontStyle.Bold);
                return;
            }

            Color statusColor = Accent;
            if (string.Equals(m.StatusText, "THERMAL", StringComparison.OrdinalIgnoreCase))
                statusColor = TextCrit;
            else if (string.Equals(m.StatusText, "WARM", StringComparison.OrdinalIgnoreCase))
                statusColor = TextWarn;
            else if (string.Equals(m.StatusText, "BATTERY", StringComparison.OrdinalIgnoreCase))
                statusColor = TextWarn;

            float x = 8 * s;
            float y = 4 * s;
            float line = 16f * s;
            float maxRight = x;
            float titleSize = 11.5f * s;
            float bodySize = 10.5f * s;

            // Header — always visible status glyph
            if (cfg.ShowStatusGlyph)
            {
                string header = $"{m.StatusGlyph}  {m.StatusText}";
                maxRight = Math.Max(maxRight, x + MeasureText(g, header, titleSize));
                DrawLine(g, header, x, y, statusColor, titleSize, FontStyle.Bold);
                y += line + 2 * s;
            }

            // Build only rows with real data — never print N/A
            var rows = new List<(string text, Color color)>();

            if (cfg.ShowFps && m.Fps.HasValue)
            {
                string src = string.IsNullOrEmpty(m.FpsSource) ? "" : $"  {m.FpsSource}";
                rows.Add(($"FPS   {m.Fps.Value,6:0.0}{src}", Accent));
            }

            if (cfg.ShowCpuTemp)
            {
                if (m.CpuTempC.HasValue)
                {
                    Color c = TempColor(m.CpuTempC.Value, cfg.CpuWarnC, cfg.CpuCritC);
                    string src = string.IsNullOrEmpty(m.CpuTempSource) ? "" : " " + m.CpuTempSource;
                    rows.Add(($"CPU   {cfg.FormatTemp(m.CpuTempC.Value),5}{src}   {m.CpuUsagePct,4:0}%", c));
                }
                else if (m.CpuUsagePct > 0.05)
                {
                    rows.Add(($"CPU   {m.CpuUsagePct,5:0.0}%", Accent));
                }
                AddFanRow(rows, m.Fan1Name, m.Fan1Rpm);
                AddFanRow(rows, m.Fan2Name, m.Fan2Rpm);
            }

            if (cfg.ShowIgpuTemp)
            {
                if (m.IgpuTempC.HasValue)
                {
                    Color c = TempColor(m.IgpuTempC.Value, cfg.IgpuWarnC, cfg.IgpuCritC);
                    string u = m.IgpuUtilPct.HasValue ? $"   {m.IgpuUtilPct.Value:0}%" : "";
                    rows.Add(($"iGPU  {cfg.FormatTemp(m.IgpuTempC.Value),5}{u}", c));
                }
                else if (m.IgpuUtilPct.HasValue)
                {
                    rows.Add(($"iGPU  {m.IgpuUtilPct.Value,5:0}%", Accent));
                }
            }

            if (cfg.ShowDgpuTemp && m.DgpuTempC.HasValue)
            {
                Color c = TempColor(m.DgpuTempC.Value, cfg.DgpuWarnC, cfg.DgpuCritC);
                rows.Add(($"dGPU  {cfg.FormatTemp(m.DgpuTempC.Value),5}", c));
            }

            if (cfg.ShowGpuUtil && m.DgpuUtilPct.HasValue)
            {
                string clk = m.DgpuClockMhz.HasValue ? $"  {m.DgpuClockMhz.Value:0}MHz" : "";
                string ps = string.IsNullOrEmpty(m.DgpuPState) ? "" : $"  {m.DgpuPState}";
                rows.Add(($"1070  {m.DgpuUtilPct.Value,5:0.0}%{clk}{ps}", Accent));
            }

            if (cfg.ShowVram && m.VramUsedMiB.HasValue && m.VramTotalMiB.HasValue)
            {
                rows.Add(($"VRAM  {m.VramUsedMiB.Value:0}/{m.VramTotalMiB.Value:0} MiB", Accent));
            }

            if (cfg.ShowPower && m.DgpuPowerW.HasValue)
            {
                string lim = m.DgpuPowerLimitW.HasValue ? $" / {m.DgpuPowerLimitW.Value:0}W" : "W";
                rows.Add(($"1070  {m.DgpuPowerW.Value,5:0.0}{lim}", Accent));
            }

            if (cfg.ShowSystemRam && m.SystemRamTotalGiB > 0.1)
            {
                rows.Add(($"RAM   {m.SystemRamUsedGiB:0.0}/{m.SystemRamTotalGiB:0.0} GiB  {m.SystemRamUsedPct:0}%", Accent));
            }

            // Power source (compact)
            rows.Add((m.OnAcPower ? "AC" : "BATTERY", m.OnAcPower ? DimAccent : TextWarn));

            foreach (var row in rows)
            {
                maxRight = Math.Max(maxRight, x + MeasureText(g, row.text, bodySize));
                DrawLine(g, row.text, x, y, row.color, bodySize, FontStyle.Bold);
                y += line;
            }
            RequestFit(maxRight, y);
        }

        private static float MeasureText(Graphics g, string text, float size)
        {
            if (size < 7f) size = 7f;
            using var font = new Font("Consolas", size, FontStyle.Bold, GraphicsUnit.Pixel);
            return g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        private void RequestFit(float right, float bottom)
        {
            if (_userSized || _resizing)
                return;
            int wantW = Math.Clamp((int)Math.Ceiling(right) + 18, 64, 900);
            int wantH = Math.Clamp((int)Math.Ceiling(bottom) + 16, 32, 700);
            _config.NaturalWidth = wantW;
            _config.NaturalHeight = wantH;
            if (Math.Abs(ClientSize.Width - wantW) <= 2 && Math.Abs(ClientSize.Height - wantH) <= 2)
                return;
            if (_fitQueued || !IsHandleCreated)
                return;
            _fitQueued = true;
            BeginInvoke(new Action(() =>
            {
                _fitQueued = false;
                if (IsDisposed) return;
                _inCompose = true;
                try { ClientSize = new Size(wantW, wantH); }
                finally { _inCompose = false; }
                ComposeAndPush();
            }));
        }

        private Color TempColor(double t, int warn, int crit)
        {
            if (t >= crit) return TextCrit;
            if (t >= warn) return TextWarn;
            return Accent;
        }

        private void AddFanRow(List<(string text, Color color)> rows, string name, int? rpm)
        {
            if (rpm is not > 0)
                return;
            string label = string.IsNullOrWhiteSpace(name) ? "Fan" : name.Trim();
            rows.Add(($"{label}  {rpm.Value,5} rpm", Accent));
        }

        private Rectangle GripRect()
        {
            int grip = Math.Max(18, (int)(22 * _scale));
            return new Rectangle(Math.Max(0, ClientSize.Width - grip), Math.Max(0, ClientSize.Height - grip), grip, grip);
        }

        private void DrawResizeCorner(Graphics g)
        {
            Rectangle grip = GripRect();
            using var pen = new Pen(Color.FromArgb(210, Accent), 1.5f);
            int right = grip.Right - 3;
            int bottom = grip.Bottom - 3;
            for (int i = 0; i < 3; i++)
            {
                int offset = i * 4;
                g.DrawLine(pen, right - 10 + offset, bottom, right, bottom - 10 + offset);
            }
        }

        private static void DrawLine(Graphics g, string text, float x, float y, Color color, float size, FontStyle style)
        {
            if (size < 7f) size = 7f;
            try
            {
                using var fam = new FontFamily("Consolas");
                using var path = new GraphicsPath();
                path.AddString(text, fam, (int)style, size, new PointF(x, y), StringFormat.GenericTypographic);
                float stroke = Math.Clamp(size * 0.16f, 2.0f, 4.0f);
                using (var pen = new Pen(Outline, stroke) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
            }
            catch
            {
                using var font = new Font(FontFamily.GenericMonospace, Math.Max(8f, size * 0.75f), style, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(color);
                g.DrawString(text, font, brush, x, y);
            }
        }

        private void PushLayered(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                old = SelectObject(memDc, hBmp);
                var src = new POINT { x = 0, y = 0 };
                var dst = new POINT { x = Left, y = Top };
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ---- drag (moves whole window; does not scramble content) ----
        private void OnDragDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || EffectiveClickThrough) return;
            if (GripRect().Contains(e.Location))
            {
                _resizing = true;
                _userSized = true;
                _config.UserSizedFrame = true;
                if (_config.NaturalWidth < 64)
                    _config.NaturalWidth = Math.Max(64, ClientSize.Width);
                if (_config.NaturalHeight < 32)
                    _config.NaturalHeight = Math.Max(32, ClientSize.Height);
                _dragging = false;
                _resizeStartSize = Size;
                _resizeStartCursor = Cursor.Position;
            }
            else
            {
                _dragging = true;
                _resizing = false;
                _dragStartCursor = Cursor.Position;
                _dragStartLoc = Location;
            }
            Capture = true;
        }

        private void OnDragMove(object? sender, MouseEventArgs e)
        {
            if (_resizing)
            {
                int dx = Cursor.Position.X - _resizeStartCursor.X;
                int dy = Cursor.Position.Y - _resizeStartCursor.Y;
                int nw = Math.Clamp(_resizeStartSize.Width + dx, 140, 900);
                int nh = Math.Clamp(_resizeStartSize.Height + dy, 80, 700);
                Size = new Size(nw, nh);
                ComposeAndPush();
                return;
            }

            if (_dragging)
            {
                int dx = Cursor.Position.X - _dragStartCursor.X;
                int dy = Cursor.Position.Y - _dragStartCursor.Y;
                Location = new Point(_dragStartLoc.X + dx, _dragStartLoc.Y + dy);
                ComposeAndPush();
                return;
            }

            Cursor = GripRect().Contains(e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        private void OnDragUp(object? sender, MouseEventArgs e)
        {
            if (!_dragging && !_resizing) return;
            _dragging = false;
            _resizing = false;
            Capture = false;
            PersistGeometry();
        }

        // Panel grip cannot receive clicks on an UpdateLayeredWindow surface.
        // Resize is handled on the form, from the painted corner.
        private void OnResizeDown(object? sender, MouseEventArgs e)
        {
            OnDragDown(sender, e);
        }

        private void OnResizeMove(object? sender, MouseEventArgs e)
        {
            OnDragMove(sender, e);
        }

        private void OnResizeUp(object? sender, MouseEventArgs e)
        {
            OnDragUp(sender, e);
        }

        private void OnRightClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && ContextMenuStrip != null)
            {
                try { ContextMenuStrip.Show(Cursor.Position); } catch { }
            }
        }

        private void ApplyExtendedStyles()
        {
            try
            {
                if (!IsHandleCreated) return;
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                ex |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                if (EffectiveClickThrough)
                    ex |= WS_EX_TRANSPARENT;
                else
                    ex &= ~WS_EX_TRANSPARENT;
                SetWindowLong(Handle, GWL_EXSTYLE, ex);
                if (_resizeGrip != null)
                    _resizeGrip.Visible = false;
            }
            catch (Exception ex)
            {
                Logger.Error("ApplyExtendedStyles", ex);
            }
        }

        private bool EffectiveClickThrough => _config.ClickThrough || _gameClickThroughApplied;

        public void SetClickThrough(bool enabled)
        {
            _config.ClickThrough = enabled;
            if (!enabled)
                _gameClickThroughApplied = false;
            ApplyExtendedStyles();
            try { if (IsHandleCreated) ComposeAndPush(); } catch { /* ignore */ }
            _config.Save();
        }

        public void ForceTopMost()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated || !Visible) return;
                TopMost = true;
                SetWindowPos(
                    Handle,
                    HWND_TOPMOST,
                    0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            catch { /* ignore */ }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            const int WM_ACTIVATE = 0x0006;
            const int WM_ACTIVATEAPP = 0x001C;
            if (m.Msg == WM_WINDOWPOSCHANGING && Visible && _config.StayInFront && m.LParam != IntPtr.Zero)
            {
                try
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                    wp.hwndInsertAfter = HWND_TOPMOST;
                    Marshal.StructureToPtr(wp, m.LParam, false);
                }
                catch { /* ignore */ }
            }
            base.WndProc(ref m);
            if ((m.Msg == WM_ACTIVATE || m.Msg == WM_ACTIVATEAPP) && Visible)
            {
                try { BeginInvoke(new Action(ForceTopMost)); } catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx, cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        public void PersistGeometry()
        {
            try
            {
                if (IsDisposed) return;
                _config.WindowX = Left;
                _config.WindowY = Top;
                _config.WindowWidth = Width;
                _config.WindowHeight = Height;
                try { _config.Opacity = Opacity; } catch { }
                _config.Save();
            }
            catch (Exception ex)
            {
                Logger.Error("PersistGeometry", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _topmostTimer?.Stop();
                    _topmostTimer?.Dispose();
                    _topmostTimer = null;
                }
                catch { }
            }
            base.Dispose(disposing);
        }
    }
}
