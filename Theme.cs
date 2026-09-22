// Palette, type scale and Windows theme integration.
//
// Every colour, font and brush is created once and reused - the list repaints on hover, so
// allocating a brush per paint would churn the GC for no reason. Metrics come from one 4px
// scale (craft R8), the type scale has four sizes (R9), and there is no pure black or pure
// white anywhere (R7). Neutrals are warm, to sit with the artwork rather than fight it.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HydraClaude
{
    public class Palette
    {
        public Color WindowBg, SurfaceBg, Text, TextMuted, Hairline;
        public Color RowHover, RowSelected, Accent, AccentHover, AccentActive, AccentText;
        public Color GhostHover, LiveText, LiveBg;
        public bool Dark;

        public Brush BrText, BrMuted, BrAccent, BrAccentText, BrSurface, BrWindow;
        public Brush BrRowHover, BrRowSelected, BrLiveText, BrLiveBg;
        public Pen PnHairline, PnAccent;

        public void Bake()
        {
            BrText = new SolidBrush(Text);
            BrMuted = new SolidBrush(TextMuted);
            BrAccent = new SolidBrush(Accent);
            BrAccentText = new SolidBrush(AccentText);
            BrSurface = new SolidBrush(SurfaceBg);
            BrWindow = new SolidBrush(WindowBg);
            BrRowHover = new SolidBrush(RowHover);
            BrRowSelected = new SolidBrush(RowSelected);
            BrLiveText = new SolidBrush(LiveText);
            BrLiveBg = new SolidBrush(LiveBg);
            PnHairline = new Pen(Hairline);
            PnAccent = new Pen(Accent);
        }
    }

    public static class Theme
    {
        public static Palette P { get; private set; }

        // Four sizes is enough; hierarchy comes from weight and colour first (craft R9).
        public static Font FontTitle, FontRow, FontBody, FontChip, FontBodyBold;

        public static event EventHandler Changed;

        static Theme()
        {
            BuildFonts();
            P = BuildPalette(IsSystemDark());
            SystemEvents.UserPreferenceChanged += delegate(object s, UserPreferenceChangedEventArgs e)
            {
                if (e.Category != UserPreferenceCategory.General
                    && e.Category != UserPreferenceCategory.Color) return;
                var dark = IsSystemDark();
                if (dark == P.Dark) return;
                P = BuildPalette(dark);
                var h = Changed;
                if (h != null) h(null, EventArgs.Empty);
            };
        }

        static Font Pick(string family, float size, FontStyle style)
        {
            var f = new Font(family, size, style);
            if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
            return new Font("Segoe UI", size, style == FontStyle.Regular ? FontStyle.Bold : style);
        }

        static void BuildFonts()
        {
            FontTitle = Pick("Segoe UI Semibold", 12f, FontStyle.Regular);
            FontRow = Pick("Segoe UI Semibold", 10.5f, FontStyle.Regular);
            FontBody = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            FontBodyBold = Pick("Segoe UI Semibold", 8.25f, FontStyle.Regular);
            FontChip = Pick("Segoe UI Semibold", 8f, FontStyle.Regular);
        }

        public static bool IsSystemDark()
        {
            // Escape hatch for checking both palettes without touching the user's setting.
            var forced = Environment.GetEnvironmentVariable("HYDRACLAUDE_THEME");
            if (!string.IsNullOrEmpty(forced))
            {
                if (forced.Equals("dark", StringComparison.OrdinalIgnoreCase)) return true;
                if (forced.Equals("light", StringComparison.OrdinalIgnoreCase)) return false;
            }

            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    var v = k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        static Palette BuildPalette(bool dark)
        {
            var p = dark
                ? new Palette
                {
                    Dark = true,
                    WindowBg = Rgb(0x17, 0x15, 0x16),
                    SurfaceBg = Rgb(0x1E, 0x1B, 0x1D),
                    Text = Rgb(0xF0, 0xEA, 0xEB),
                    TextMuted = Rgb(0x9A, 0x8F, 0x91),
                    Hairline = Rgb(0x2C, 0x27, 0x29),
                    RowHover = Rgb(0x26, 0x21, 0x23),
                    RowSelected = Rgb(0x35, 0x24, 0x2B),
                    Accent = Rgb(0xE2, 0x68, 0x7C),
                    AccentHover = Rgb(0xEE, 0x7A, 0x8D),
                    AccentActive = Rgb(0xC9, 0x56, 0x6A),
                    AccentText = Rgb(0x1B, 0x17, 0x19),
                    GhostHover = Rgb(0x2A, 0x25, 0x27),
                    LiveText = Rgb(0x7B, 0xD3, 0xA0),
                    LiveBg = Rgb(0x1C, 0x2F, 0x25),
                }
                : new Palette
                {
                    Dark = false,
                    WindowBg = Rgb(0xF7, 0xF3, 0xF3),
                    SurfaceBg = Rgb(0xFD, 0xFC, 0xFC),
                    Text = Rgb(0x1B, 0x17, 0x19),
                    TextMuted = Rgb(0x7A, 0x70, 0x72),
                    Hairline = Rgb(0xEA, 0xE2, 0xE3),
                    RowHover = Rgb(0xF2, 0xED, 0xED),
                    RowSelected = Rgb(0xF9, 0xEB, 0xED),
                    Accent = Rgb(0xB2, 0x3A, 0x4C),
                    AccentHover = Rgb(0x9C, 0x31, 0x42),
                    AccentActive = Rgb(0x87, 0x28, 0x36),
                    AccentText = Rgb(0xFD, 0xFD, 0xFD),
                    GhostHover = Rgb(0xEF, 0xE8, 0xE9),
                    LiveText = Rgb(0x2C, 0x6E, 0x49),
                    LiveBg = Rgb(0xE6, 0xF1, 0xE9),
                };
            p.Bake();
            return p;
        }

        static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        // Windows draws the caption itself; without this a dark window keeps a white title bar.
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void ApplyCaption(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            int on = P.Dark ? 1 : 0;
            try { DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4); }
            catch { }
        }
    }

    // One 4px spacing scale, scaled for the monitor's DPI.
    public static class Metrics
    {
        public static float Scale = 1f;

        public static int S(int px) { return (int)Math.Round(px * Scale); }

        public static int Gutter { get { return S(20); } }
        public static int RowHeight { get { return S(60); } }
        public static int HeaderHeight { get { return S(60); } }
        public static int FooterHeight { get { return S(64); } }
    }
}
