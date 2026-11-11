using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("CrossCrease Launcher")]
[assembly: AssemblyDescription("Game-agnostic CrossCrease launcher")]
[assembly: AssemblyCompany("CrossCrease")]
[assembly: AssemblyProduct("CrossCrease")]
[assembly: AssemblyVersion(CrossCrease.BuildInfo.Version + ".0")]
[assembly: AssemblyFileVersion(CrossCrease.BuildInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(CrossCrease.BuildInfo.Version)]

namespace CrossCrease
{
    public static class BuildInfo
    {
        // Version source of truth for the launcher. Keep CHANGELOG.md in sync.
        public const string Version = "0.4.0";
        // Launcher release feed (same format as game package feeds) checked at startup.
        public const string UpdateFeed = "https://github.com/CrossCrease/launcher/releases/latest/download/crosscrease-launcher-feed.json";
        public const string ReleasesPage = "https://github.com/CrossCrease/launcher/releases/latest";
    }

    public sealed class GameProfile
    {
        public string id { get; set; }
        public string name { get; set; }
        public string system { get; set; }
        public string description { get; set; }
        public string status { get; set; }
        public string executable { get; set; }
        public string arguments { get; set; }
        public string working_directory { get; set; }
        public string required_file { get; set; }
        public string executable_relative { get; set; }
        public string arguments_template { get; set; }
        public string working_directory_relative { get; set; }
        public string required_file_relative { get; set; }
        public string[] search_roots { get; set; }
        public string accent { get; set; }
        public string cover { get; set; }
        // Where the official cover is downloaded from on the player's PC (never bundled).
        public CoverDownload cover_download { get; set; }
        // Downloadable packages (runtime, online files, ...) published by the game project.
        public string package_feed { get; set; }
        public string[] packages { get; set; }
        public LaunchOption[] launch_options { get; set; }
        public ProfileSetting[] settings { get; set; }
        public string SourceFile { get; set; }
    }

    // An additional way to start the game, such as "Play online", shown next to Play.
    public sealed class LaunchOption
    {
        public string id { get; set; }
        public string label { get; set; }
        public string[] packages { get; set; }
        public string[] settings { get; set; }
        public string executable { get; set; }
        public string arguments_template { get; set; }
        public string working_directory { get; set; }
    }

    // A per-player value asked for once in a small dialog (for example an online ID).
    public sealed class ProfileSetting
    {
        public string id { get; set; }
        public string label { get; set; }
        public string description { get; set; }
        public string pattern { get; set; }
        public string @default { get; set; }
    }

    public enum GameState { Ready, Missing, NeedsDownload, Planned }

    public sealed class LibraryItem
    {
        public GameProfile Profile;
        public GameState State;
        public Color Accent;
        public string Executable;
        public string Root;
        public bool GameFound;
        public List<string> MissingPackages = new List<string>();
        public List<FeedPackage> Updates = new List<FeedPackage>();
    }

    public sealed class ItemEventArgs : EventArgs
    {
        public LibraryItem Item;
        public Point ScreenLocation;
    }

    static class Ui
    {
        public static float Scale = 1f;

        public static readonly Color Background = Color.FromArgb(14, 16, 21);
        public static readonly Color Text = Color.FromArgb(240, 243, 247);
        public static readonly Color Muted = Color.FromArgb(158, 166, 178);
        public static readonly Color Dim = Color.FromArgb(108, 115, 127);
        public static readonly Color Brand = Color.FromArgb(39, 201, 179);
        public static readonly Color Green = Color.FromArgb(78, 210, 138);
        public static readonly Color Amber = Color.FromArgb(245, 176, 70);
        public static readonly Color Grey = Color.FromArgb(96, 103, 115);
        public static readonly Color GreyCaption = Color.FromArgb(25, 27, 32);

        // Colour of the selected cover; the window tints toward it. Animated by MainForm.
        public static Color Theme = Color.FromArgb(39, 201, 179);

        // Surfaces use a deepened version of the theme so even bright cover colours keep white text readable.
        static Color Deep { get { return Mix(Theme, Color.Black, 0.18f); } }
        public static Color TopBarColor { get { return Mix(Background, Deep, 0.30f); } }
        public static Color BarColor { get { return Mix(Background, Deep, 0.40f); } }
        public static Color LibraryTop { get { return Mix(Background, Deep, 0.36f); } }

        public static Color FromHsl(float hue, float saturation, float lightness)
        {
            float c = (1f - Math.Abs(2f * lightness - 1f)) * saturation;
            float hp = (hue % 360f) / 60f;
            float x = c * (1f - Math.Abs(hp % 2f - 1f));
            float r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; } else if (hp < 2) { r = x; g = c; } else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; } else if (hp < 5) { r = x; b = c; } else { r = c; b = x; }
            float m = lightness - c / 2f;
            return Color.FromArgb(Clamp255((r + m) * 255f), Clamp255((g + m) * 255f), Clamp255((b + m) * 255f));
        }

        static int Clamp255(float v) { return (int)Math.Max(0, Math.Min(255, Math.Round(v))); }

        // Fraction of a cover taken by a platform banner (PS4 blue bar, PS5 white/black bar) at the top.
        // Detected as the first strong horizontal colour edge in the top fifth; 0 when there is none.
        static float BannerFraction(Bitmap column)
        {
            int h = column.Height, limit = h / 5;
            var rows = new Color[limit + 1];
            for (int y = 0; y <= limit; y++)
            {
                int r = 0, g = 0, b = 0;
                for (int x = 0; x < column.Width; x++) { var c = column.GetPixel(x, y); r += c.R; g += c.G; b += c.B; }
                rows[y] = Color.FromArgb(r / column.Width, g / column.Width, b / column.Width);
            }
            int bestRow = -1; float bestEdge = 0f;
            for (int y = h / 25; y < limit; y++)
            {
                float edge = Math.Abs(rows[y].R - rows[y + 1].R) + Math.Abs(rows[y].G - rows[y + 1].G) + Math.Abs(rows[y].B - rows[y + 1].B);
                if (edge > bestEdge) { bestEdge = edge; bestRow = y; }
            }
            return bestEdge > 90f ? (bestRow + 2) / (float)h : 0f;
        }

        // Picks the main colour of the cover art itself: the platform banner and the rating/legal strip
        // are skipped, saturated pixels near the centre (usually the jersey) count most, and the result
        // keeps the cover's own hue at full strength. Returns null for colourless artwork.
        public static Color? Dominant(Image image)
        {
            const int sw = 48, sh = 64, bins = 36;
            float top;
            using (var column = new Bitmap(16, 200, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(column))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(image, new Rectangle(0, 0, 16, 200), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                }
                top = BannerFraction(column);
            }
            float bottom = 0.88f;
            var weight = new float[bins];
            var rs = new float[bins]; var gs = new float[bins]; var bs = new float[bins];
            using (var sample = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(sample))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(image, new Rectangle(0, 0, sw, sh), 0, image.Height * top, image.Width, image.Height * (bottom - top), GraphicsUnit.Pixel);
                }
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                    {
                        var c = sample.GetPixel(x, y);
                        float max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255f, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255f;
                        float chroma = max - min, light = (max + min) / 2f;
                        if (chroma < 0.14f || light < 0.07f || light > 0.93f) continue;
                        float dx = Math.Abs(x / (float)(sw - 1) - 0.5f), dy = Math.Abs(y / (float)(sh - 1) - 0.55f);
                        float centre = 1f - 0.9f * Math.Min(1f, (dx * dx + dy * dy) * 2.2f);
                        float w = chroma * chroma * centre;
                        int bin = (int)(c.GetHue() / 360f * bins) % bins;
                        weight[bin] += w; rs[bin] += c.R * w; gs[bin] += c.G * w; bs[bin] += c.B * w;
                    }
            }
            int best = -1; float bestWeight = 0;
            for (int i = 0; i < bins; i++)
            {
                float around = weight[i] + 0.6f * (weight[(i + 1) % bins] + weight[(i + bins - 1) % bins]) +
                    0.25f * (weight[(i + 2) % bins] + weight[(i + bins - 2) % bins]);
                if (around > bestWeight) { bestWeight = around; best = i; }
            }
            if (best < 0 || bestWeight < 3f) return null;
            float sw2 = 0, r = 0, gr = 0, b = 0;
            for (int k = -1; k <= 1; k++)
            {
                int i = (best + k + bins) % bins;
                sw2 += weight[i]; r += rs[i]; gr += gs[i]; b += bs[i];
            }
            var avg = Color.FromArgb(Clamp255(r / sw2), Clamp255(gr / sw2), Clamp255(b / sw2));
            return FromHsl(avg.GetHue(), Math.Max(0.6f, Math.Min(0.95f, avg.GetSaturation() * 1.15f)), Math.Max(0.44f, Math.Min(0.58f, avg.GetBrightness())));
        }

        public static int S(float value) { return (int)Math.Round(value * Scale); }
        public static float F(float value) { return value * Scale; }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static float Luma(Color c) { return (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f; }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void HighQuality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        static FontFamily heavyFamily;
        static FontStyle heavyStyle;

        // "Segoe UI Black" when installed, otherwise bold Segoe UI.
        public static FontFamily Heavy(out FontStyle style)
        {
            if (heavyFamily == null)
            {
                try { heavyFamily = new FontFamily("Segoe UI Black"); heavyStyle = FontStyle.Regular; }
                catch (ArgumentException) { heavyFamily = new FontFamily("Segoe UI"); heavyStyle = FontStyle.Bold; }
            }
            style = heavyStyle;
            return heavyFamily;
        }

        public static Font HeavyFont(float points)
        {
            FontStyle style;
            var family = Heavy(out style);
            return new Font(family, points, style);
        }

        public static readonly Color Blue = Color.FromArgb(92, 170, 255);

        public static string StateCaption(LibraryItem item)
        {
            if (item.State == GameState.Ready && item.Updates.Count > 0) return "Update available";
            switch (item.State)
            {
                case GameState.Ready: return "Ready to play";
                case GameState.Missing: return "Not found";
                case GameState.NeedsDownload: return "Download needed";
                default: return "Coming soon";
            }
        }

        public static string StateTitle(LibraryItem item)
        {
            if (item.State == GameState.Ready && item.Updates.Count > 0) return "Update available";
            switch (item.State)
            {
                case GameState.Ready: return "Ready to play";
                case GameState.Missing: return "Game not found";
                case GameState.NeedsDownload: return "Download needed";
                default: return "Coming soon";
            }
        }

        public static Color StateColor(LibraryItem item)
        {
            if (item.State == GameState.Ready && item.Updates.Count > 0) return Blue;
            switch (item.State)
            {
                case GameState.Ready: return Green;
                case GameState.Missing: return Amber;
                case GameState.NeedsDownload: return Blue;
                default: return Dim;
            }
        }
    }

    // Generates CrossCrease-branded poster artwork, or frames a user-supplied cover image.
    static class CoverArt
    {
        public static Bitmap Render(LibraryItem item, Image custom, int w, int h, bool grey, Color? fadeTo)
        {
            var art = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(art))
            {
                Ui.HighQuality(g);
                if (custom != null) DrawCustom(g, custom, w, h);
                else DrawGenerated(g, item, w, h);
            }
            if (grey) art = Desaturate(art, 0.5f);
            if (fadeTo.HasValue)
            {
                using (var g = Graphics.FromImage(art))
                {
                    float top = h * 0.72f;
                    var fill = new RectangleF(0, top, w, h - top);
                    using (var brush = new LinearGradientBrush(new RectangleF(0, top - 1, w, h - top + 2),
                        Color.FromArgb(0, fadeTo.Value), fadeTo.Value, 90f))
                        g.FillRectangle(brush, fill);
                }
            }
            return art;
        }

        // Box art is usually wider than the 2:3 poster. Rather than cropping its sides, fit it to the
        // poster width at the top and fill the remaining strip with a blurred extension of the image.
        static void DrawCustom(Graphics g, Image image, int w, int h)
        {
            float imageAspect = image.Width / (float)image.Height;
            float posterAspect = w / (float)h;
            if (imageAspect <= posterAspect * 1.04f)
            {
                DrawFill(g, image, new Rectangle(0, 0, w, h));
                return;
            }
            using (var tiny = new Bitmap(8, 12, PixelFormat.Format32bppPArgb))
            {
                using (var tg = Graphics.FromImage(tiny))
                {
                    tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    // Sample the lower part of the cover so the extension continues its colours.
                    tg.DrawImage(image, new Rectangle(0, 0, 8, 12),
                        0, image.Height * 0.55f, image.Width, image.Height * 0.45f, GraphicsUnit.Pixel);
                }
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    var previous = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(tiny, new Rectangle(0, 0, w, h), 0, 0, 8, 12, GraphicsUnit.Pixel, attributes);
                    g.InterpolationMode = previous;
                }
            }
            float fittedHeight = w / imageAspect;
            g.DrawImage(image, 0f, 0f, (float)w, fittedHeight);
            // Soften the seam between the artwork and the blurred extension.
            float seam = Math.Min(h * 0.06f, fittedHeight * 0.2f);
            var seamRect = new RectangleF(0, fittedHeight - seam, w, seam * 2f);
            using (var brush = new LinearGradientBrush(new RectangleF(0, seamRect.Y - 1, w, seamRect.Height + 2),
                Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0), 90f))
                g.FillRectangle(brush, seamRect);
        }

        static void DrawFill(Graphics g, Image image, Rectangle area)
        {
            float scale = Math.Max(area.Width / (float)image.Width, area.Height / (float)image.Height);
            float dw = image.Width * scale, dh = image.Height * scale;
            g.DrawImage(image, area.X + (area.Width - dw) / 2f, area.Y + (area.Height - dh) / 2f, dw, dh);
        }

        static void DrawGenerated(Graphics g, LibraryItem item, int w, int h)
        {
            Color accent = item.Accent;
            using (var background = new LinearGradientBrush(new Rectangle(0, -1, w, h + 2), Color.Black, Color.Black, 90f))
            {
                var blend = new ColorBlend(3);
                blend.Colors = new[] { Ui.Mix(accent, Color.White, 0.2f), accent, Ui.Mix(accent, Color.Black, 0.8f) };
                blend.Positions = new[] { 0f, 0.42f, 1f };
                background.InterpolationColors = blend;
                g.FillRectangle(background, 0, 0, w, h);
            }

            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(w * 0.78f - w * 0.95f, h * 0.08f - w * 0.95f, w * 1.9f, w * 1.9f);
                using (var glow = new PathGradientBrush(glowPath))
                {
                    glow.CenterColor = Color.FromArgb(85, 255, 255, 255);
                    glow.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(glow, glowPath);
                }
            }

            // Ice markings: a faceoff circle behind the number and the goal crease at the bottom.
            float cx = w / 2f, cy = h * 0.53f;
            using (var sticks = new Pen(Color.FromArgb(16, 255, 255, 255), w * 0.045f))
            {
                sticks.StartCap = LineCap.Round;
                sticks.EndCap = LineCap.Round;
                g.DrawLine(sticks, w * 0.06f, h * 0.14f, w * 0.88f, h * 0.86f);
                g.DrawLine(sticks, w * 0.94f, h * 0.14f, w * 0.12f, h * 0.86f);
            }
            using (var line = new Pen(Color.FromArgb(42, 255, 255, 255), Math.Max(1f, w * 0.012f)))
            {
                float r = w * 0.4f;
                g.DrawEllipse(line, cx - r, cy - r, r * 2, r * 2);
                g.DrawLine(line, cx - r - w * 0.06f, cy - r * 0.35f, cx - r, cy - r * 0.35f);
                g.DrawLine(line, cx - r - w * 0.06f, cy + r * 0.35f, cx - r, cy + r * 0.35f);
                g.DrawLine(line, cx + r, cy - r * 0.35f, cx + r + w * 0.06f, cy - r * 0.35f);
                g.DrawLine(line, cx + r, cy + r * 0.35f, cx + r + w * 0.06f, cy + r * 0.35f);

                float goal = h * 0.92f, crease = w * 0.19f;
                using (var creaseFill = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                    g.FillPie(creaseFill, cx - crease, goal - crease, crease * 2, crease * 2, 180, 180);
                g.DrawArc(line, cx - crease, goal - crease, crease * 2, crease * 2, 180, 180);
                g.DrawLine(line, 0, goal, w, goal);
            }

            string prefix, token;
            SplitName(item.Profile.name, out prefix, out token);
            FontStyle heavyStyle;
            var heavy = Ui.Heavy(out heavyStyle);
            FillText(g, token, heavy, heavyStyle, new RectangleF(w * 0.12f, h * 0.415f, w * 0.76f, h * 0.25f), Color.White, h * 0.012f);
            if (prefix.Length > 0)
                FillText(g, prefix.ToUpperInvariant(), heavy, heavyStyle, new RectangleF(w * 0.24f, h * 0.30f, w * 0.52f, h * 0.075f), Color.FromArgb(240, 255, 255, 255), h * 0.008f);

            using (var small = new Font("Segoe UI", Math.Max(6f, h * 0.031f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
            {
                g.DrawString("CROSSCREASE", small, white, w * 0.06f, h * 0.035f);
                string platform = Platform(item.Profile);
                if (platform.Length > 0)
                {
                    var size = g.MeasureString(platform, small);
                    var badge = new RectangleF(w * 0.94f - size.Width - h * 0.012f, h * 0.035f - h * 0.004f, size.Width + h * 0.012f, size.Height + h * 0.006f);
                    using (var path = Ui.Rounded(badge, h * 0.012f))
                    using (var pen = new Pen(Color.FromArgb(170, 255, 255, 255), Math.Max(1f, h * 0.004f)))
                        g.DrawPath(pen, path);
                    g.DrawString(platform, small, white, badge.X + h * 0.006f, badge.Y + h * 0.003f);
                }
            }
        }

        static void FillText(Graphics g, string text, FontFamily family, FontStyle style, RectangleF box, Color color, float shadow)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var path = new GraphicsPath())
            {
                path.AddString(text, family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
                var bounds = path.GetBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                float scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
                using (var matrix = new Matrix())
                {
                    matrix.Translate(box.X + box.Width / 2f, box.Y + box.Height / 2f);
                    matrix.Scale(scale, scale);
                    matrix.Translate(-(bounds.X + bounds.Width / 2f), -(bounds.Y + bounds.Height / 2f));
                    path.Transform(matrix);
                }
                using (var offset = new Matrix())
                using (var shadowPath = (GraphicsPath)path.Clone())
                using (var shadowBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                {
                    offset.Translate(0, shadow);
                    shadowPath.Transform(offset);
                    g.FillPath(shadowBrush, shadowPath);
                }
                using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
            }
        }

        static Bitmap Desaturate(Bitmap source, float brightness)
        {
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            float r = 0.299f * brightness, gr = 0.587f * brightness, b = 0.114f * brightness;
            var matrix = new ColorMatrix(new[] {
                new[] { r, r, r, 0f, 0f },
                new[] { gr, gr, gr, 0f, 0f },
                new[] { b, b, b, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0.02f, 0.022f, 0.028f, 0f, 1f }
            });
            using (var attributes = new ImageAttributes())
            using (var g = Graphics.FromImage(result))
            {
                attributes.SetColorMatrix(matrix);
                g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            source.Dispose();
            return result;
        }

        public static void SplitName(string name, out string prefix, out string token)
        {
            name = (name ?? "").Trim();
            token = MainForm.ProfileToken(name);
            int number;
            int index = name.LastIndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (int.TryParse(token, out number) && index >= 0)
                prefix = name.Remove(index, token.Length).Trim();
            else
                prefix = "";
        }

        public static string Platform(GameProfile profile)
        {
            string system = profile.system ?? "";
            if (Contains(system, "PlayStation 5") || Contains(system, "PS5")) return "PS5";
            if (Contains(system, "PlayStation 4") || Contains(system, "PS4")) return "PS4";
            if (Contains(system, "Xbox Series")) return "XSX";
            if (Contains(system, "Xbox One")) return "XB1";
            if (Contains(system, "Windows") || Contains(system, "PC")) return "PC";
            return "";
        }

        static bool Contains(string value, string part) { return value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }
    }

    public enum ButtonKind { Primary, Secondary, Ghost }

    public sealed class FlatButton : Control
    {
        ButtonKind kind = ButtonKind.Secondary;
        Color accent = Ui.Brand;
        bool playIcon, hover, pressed;

        public ButtonKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public Color Accent { get { return accent; } set { accent = value; Invalidate(); } }
        public bool ShowPlayIcon { get { return playIcon; } set { playIcon = value; Invalidate(); } }

        public FlatButton()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            BackColor = Color.Transparent;
            ForeColor = Ui.Text;
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
        }

        public int PreferredWidth()
        {
            int text = TextRenderer.MeasureText(Text, Font).Width;
            int pad = kind == ButtonKind.Ghost ? Ui.S(14) : Ui.S(24);
            return text + pad * 2 + (playIcon ? Ui.S(22) : 0);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            if (!Enabled) { hover = false; pressed = false; }
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return (keyData & Keys.KeyCode) == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { e.Handled = true; OnClick(EventArgs.Empty); }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.HighQuality(g);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Color fill, fore;
            if (kind == ButtonKind.Primary)
            {
                if (!Enabled) { fill = Color.FromArgb(40, 255, 255, 255); fore = Color.FromArgb(125, 255, 255, 255); }
                else
                {
                    fill = pressed ? Ui.Mix(accent, Color.Black, 0.18f) : hover ? Ui.Mix(accent, Color.White, 0.16f) : accent;
                    fore = Ui.Luma(accent) > 0.55f ? Color.FromArgb(12, 14, 18) : Color.White;
                }
            }
            else if (kind == ButtonKind.Secondary)
            {
                fill = Color.FromArgb(!Enabled ? 18 : pressed ? 28 : hover ? 60 : 38, 255, 255, 255);
                fore = Enabled ? Ui.Text : Color.FromArgb(115, 255, 255, 255);
            }
            else
            {
                fill = Color.FromArgb(!Enabled ? 0 : pressed ? 22 : hover ? 34 : 0, 255, 255, 255);
                fore = !Enabled ? Ui.Dim : hover ? Ui.Text : Ui.Muted;
            }

            if (fill.A > 0)
                using (var path = Ui.Rounded(rect, Ui.F(8)))
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            if (Focused && ShowFocusCues)
            {
                var ring = rect;
                ring.Inflate(-Ui.F(2.5f), -Ui.F(2.5f));
                using (var path = Ui.Rounded(ring, Ui.F(6)))
                using (var pen = new Pen(Color.FromArgb(170, fore), Ui.F(1.5f)))
                    g.DrawPath(pen, path);
            }

            var size = g.MeasureString(Text, Font);
            float icon = playIcon ? Ui.F(13) : 0f, iconGap = playIcon ? Ui.F(10) : 0f;
            float x = (Width - (icon + iconGap + size.Width)) / 2f, cy = Height / 2f;
            using (var brush = new SolidBrush(fore))
            {
                if (playIcon)
                {
                    g.FillPolygon(brush, new[] {
                        new PointF(x, cy - icon * 0.6f), new PointF(x + icon, cy), new PointF(x, cy + icon * 0.6f)
                    });
                    x += icon + iconGap;
                }
                g.DrawString(Text, Font, brush, x, cy - size.Height / 2f);
            }
        }
    }

    sealed class TopBar : Control
    {
        public readonly FlatButton ImportButton = new FlatButton { Kind = ButtonKind.Ghost, Text = "Import profile" };
        public readonly FlatButton RefreshButton = new FlatButton { Kind = ButtonKind.Ghost, Text = "Refresh" };
        public readonly FlatButton FolderButton = new FlatButton { Kind = ButtonKind.Ghost, Text = "Profiles folder" };
        // Shown when a newer launcher release exists.
        public readonly FlatButton UpdateButton = new FlatButton { Kind = ButtonKind.Secondary, Text = "Launcher update", Visible = false };

        public TopBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(64);
            BackColor = Ui.Background;
            foreach (var button in new[] { ImportButton, RefreshButton, FolderButton, UpdateButton })
            {
                button.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
                Controls.Add(button);
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Width - Ui.S(34), h = Ui.S(36);
            foreach (var button in new[] { FolderButton, RefreshButton, ImportButton, UpdateButton })
            {
                if (!button.Visible) continue;
                int w = button.PreferredWidth();
                x -= w;
                button.SetBounds(x, (Height - h) / 2, w, h);
                x -= Ui.S(4);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.HighQuality(g);
            g.Clear(Ui.TopBarColor);
            float x = Ui.F(48), cy = Height / 2f, r = Ui.F(11);
            using (var brand = new SolidBrush(Ui.Mix(Ui.Theme, Color.White, 0.1f)))
                g.FillPie(brand, x, cy - r + Ui.F(5), r * 2, r * 2, 180, 180);
            using (var line = new Pen(Color.FromArgb(200, 255, 255, 255), Ui.F(2)))
                g.DrawLine(line, x - Ui.F(3), cy + Ui.F(6), x + r * 2 + Ui.F(3), cy + Ui.F(6));
            using (var font = Ui.HeavyFont(14f))
            using (var versionFont = new Font("Segoe UI", 8.5f))
            using (var text = new SolidBrush(Ui.Text))
            using (var dim = new SolidBrush(Ui.Dim))
            {
                var size = g.MeasureString("CROSSCREASE", font);
                float tx = x + r * 2 + Ui.F(12);
                g.DrawString("CROSSCREASE", font, text, tx, cy - size.Height / 2f);
                string version = "v" + BuildInfo.Version;
                var versionSize = g.MeasureString(version, versionFont);
                g.DrawString(version, versionFont, dim, tx + size.Width + Ui.F(2), cy - versionSize.Height / 2f + Ui.F(2));
            }
        }
    }

    // Slim selection bar: thumbnail, title, one status line, and actions on the right.
    sealed class HeroPanel : Control
    {
        public LibraryItem Item;
        public Bitmap Poster;
        public string Detail = "";
        // Download/install progress (0..1), or a negative value when nothing is running.
        public float Progress = -1f;
        public readonly FlatButton PrimaryButton = new FlatButton { Kind = ButtonKind.Primary, Text = "Play" };
        public readonly FlatButton SettingsButton = new FlatButton { Kind = ButtonKind.Secondary, Text = "Game settings" };
        public readonly FlatButton FolderButton = new FlatButton { Kind = ButtonKind.Secondary, Text = "Open folder" };
        FlatButton[] shownButtons = new FlatButton[0];
        int buttonsLeft;
        Bitmap cache;

        public HeroPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(92);
            BackColor = Ui.Background;
            foreach (var button in new[] { PrimaryButton, SettingsButton, FolderButton })
            {
                button.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
                Controls.Add(button);
            }
        }

        int PadX { get { return Ui.S(48); } }
        public Size PosterSize { get { int h = Ui.S(64); return new Size(h * 2 / 3, h); } }
        Rectangle PosterRect { get { var s = PosterSize; return new Rectangle(PadX, (Height - s.Height) / 2, s.Width, s.Height); } }

        public void SetButtons(params FlatButton[] buttons)
        {
            shownButtons = buttons;
            foreach (var button in buttons)
                if (!Controls.Contains(button))
                {
                    button.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
                    Controls.Add(button);
                }
            var stale = new List<Control>();
            foreach (Control control in Controls)
            {
                bool shown = Array.IndexOf(buttons, control) >= 0;
                control.Visible = shown;
                if (!shown && control != PrimaryButton && control != SettingsButton && control != FolderButton) stale.Add(control);
            }
            foreach (var control in stale) { Controls.Remove(control); control.Dispose(); }
            PerformLayout();
        }

        public void SetContent()
        {
            DisposeCache();
            PerformLayout();
            Invalidate(true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = Ui.S(40), gap = Ui.S(10), total = 0;
            var widths = new int[shownButtons.Length];
            for (int i = 0; i < shownButtons.Length; i++)
            {
                widths[i] = Math.Max(Ui.S(shownButtons[i] == PrimaryButton ? 128 : 0), shownButtons[i].PreferredWidth());
                total += widths[i] + (i > 0 ? gap : 0);
            }
            int x = Width - PadX - total;
            buttonsLeft = x;
            for (int i = 0; i < shownButtons.Length; i++)
            {
                shownButtons[i].SetBounds(x, (Height - h) / 2, widths[i], h);
                x += widths[i] + gap;
            }
            DisposeCache();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DisposeCache();
            Invalidate(true);
        }

        void DisposeCache()
        {
            if (cache != null) { cache.Dispose(); cache = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCache();
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            if (cache == null) cache = Render();
            e.Graphics.DrawImageUnscaled(cache, 0, 0);
        }

        Bitmap Render()
        {
            int w = Width, h = Height;
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                Ui.HighQuality(g);
                g.Clear(Ui.BarColor);
                Color tint = Ui.Theme;

                int glowWidth = Math.Max(1, w * 2 / 3);
                using (var glow = new LinearGradientBrush(new Rectangle(-1, 0, glowWidth + 2, h), Color.FromArgb(60, tint), Color.FromArgb(0, tint), 0f))
                    g.FillRectangle(glow, 0, 0, glowWidth, h);
                using (var line = new Pen(Color.FromArgb(16, 255, 255, 255), Math.Max(1f, Ui.F(1))))
                {
                    g.DrawLine(line, 0, 0, w, 0);
                    g.DrawLine(line, 0, h - 1, w, h - 1);
                }

                float x = PadX;
                if (Poster != null)
                {
                    var rect = PosterRect;
                    using (var path = Ui.Rounded(rect, Ui.F(6)))
                    {
                        using (var texture = new TextureBrush(Poster, WrapMode.Clamp))
                        {
                            texture.TranslateTransform(rect.X, rect.Y);
                            texture.ScaleTransform(rect.Width / (float)Poster.Width, rect.Height / (float)Poster.Height);
                            g.FillPath(texture, path);
                        }
                        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, Ui.F(1))))
                            g.DrawPath(pen, path);
                    }
                    x = rect.Right + Ui.F(18);
                }
                DrawText(g, x, Math.Max(x + Ui.F(120), buttonsLeft - Ui.F(28)));
                if (Progress >= 0f)
                {
                    float barHeight = Math.Max(2f, Ui.F(3));
                    using (var track = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
                        g.FillRectangle(track, 0, h - barHeight, w, barHeight);
                    using (var fill = new SolidBrush(Ui.Mix(tint, Color.White, 0.25f)))
                        g.FillRectangle(fill, 0, h - barHeight, w * Math.Min(1f, Progress), barHeight);
                }
            }
            return bitmap;
        }

        void DrawText(Graphics g, float x, float right)
        {
            float width = right - x;
            string title, label;
            if (Item == null) { title = "No games yet"; label = "CROSSCREASE LIBRARY"; }
            else
            {
                title = Item.Profile.name;
                label = string.IsNullOrWhiteSpace(Item.Profile.system) ? "GAME PROFILE" : Item.Profile.system.ToUpperInvariant();
            }
            using (var titleFont = Ui.HeavyFont(17f))
            using (var labelFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold))
            using (var statusFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold))
            using (var detailFont = new Font("Segoe UI", 9f))
            using (var textBrush = new SolidBrush(Ui.Text))
            using (var mutedBrush = new SolidBrush(Ui.Muted))
            using (var dimBrush = new SolidBrush(Ui.Dim))
            using (var single = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
            using (var pathFormat = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisPath })
            {
                float cy = Height / 2f;
                var titleSize = g.MeasureString(title, titleFont);
                float titleY = cy - titleSize.Height + Ui.F(4);
                g.DrawString(title, titleFont, textBrush, new RectangleF(x - Ui.F(2), titleY, width, titleSize.Height), single);

                float y = cy + Ui.F(4), cursor = x;
                var labelSize = g.MeasureString(label, labelFont);
                g.DrawString(label, labelFont, mutedBrush, cursor, y + Ui.F(1));
                cursor += labelSize.Width + Ui.F(8);
                if (Item != null)
                {
                    float dot = Ui.F(7);
                    string state = Ui.StateTitle(Item);
                    var statusSize = g.MeasureString(state, statusFont);
                    float lineMid = y + statusSize.Height / 2f;
                    using (var dotBrush = new SolidBrush(Ui.StateColor(Item)))
                        g.FillEllipse(dotBrush, cursor, lineMid - dot / 2f, dot, dot);
                    cursor += dot + Ui.F(6);
                    g.DrawString(state, statusFont, textBrush, cursor, y);
                    cursor += statusSize.Width + Ui.F(10);
                }
                string detail = Item == null ? "Import a game profile to build your library." : (Detail ?? "");
                if (right - cursor > Ui.F(60))
                    g.DrawString(detail, detailFont, dimBrush, new RectangleF(cursor, y + Ui.F(1), right - cursor, Ui.F(20)),
                        Item != null && Item.State == GameState.Ready && Item.Updates.Count == 0 ? pathFormat : single);
            }
        }
    }

    // Raises the system timer resolution only while something is animating, so frame timers tick
    // at a steady ~120 Hz instead of the default 15.6 ms granularity.
    static class FrameClock
    {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
        static int users;

        public static void Acquire()
        {
            if (users++ == 0) try { timeBeginPeriod(1); } catch { }
        }

        public static void Release()
        {
            if (users > 0 && --users == 0) try { timeEndPeriod(1); } catch { }
        }

        // Exact critically damped spring step: frame-rate independent and keeps its velocity when
        // the target changes mid-flight, so interrupted animations never jump.
        public static void Spring(ref float value, ref float velocity, float target, float omega, float dt)
        {
            float y = value - target;
            float e = (float)Math.Exp(-omega * dt);
            float k = (velocity + omega * y) * dt;
            value = target + (y + k) * e;
            velocity = (velocity - omega * k) * e;
        }
    }

    sealed class LibraryView : Control
    {
        const float LiftScale = 0.06f;
        const float HoverOmega = 19f;   // settles in ~0.2 s
        const float ScrollOmega = 22f;

        readonly List<LibraryItem> items = new List<LibraryItem>();
        readonly Timer animation = new Timer { Interval = 8 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastTicks;
        bool animating;
        float[] lift = new float[0], liftVelocity = new float[0];
        Bitmap[] restCards = new Bitmap[0], liftedCards = new Bitmap[0];
        Bitmap glowMask;
        int selected = -1, hover = -1, scroll;
        float scrollPos, scrollTarget, scrollVelocity;
        bool draggingThumb, thumbHover;
        int dragOriginY, dragOriginScroll;

        public Func<LibraryItem, int, int, Bitmap> PosterProvider;
        public Func<LibraryItem, Color> CaptionColorProvider;
        public event EventHandler SelectionChanged;
        public event EventHandler<ItemEventArgs> ItemActivated;
        public event EventHandler<ItemEventArgs> ItemMenuRequested;

        public LibraryView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Ui.Background;
            animation.Tick += delegate { StepAnimation(); };
        }

        public int CardWidth { get { return Ui.S(172); } }
        public int PosterHeight { get { return CardWidth * 3 / 2; } }
        int CaptionHeight { get { return Ui.S(42); } }
        int CardHeight { get { return PosterHeight + CaptionHeight; } }
        int GapX { get { return Ui.S(20); } }
        int GapY { get { return Ui.S(28); } }
        int PadX { get { return Ui.S(48); } }
        int HeaderHeight { get { return Ui.S(58); } }
        int GlowMargin { get { return Ui.S(36); } }
        int Columns { get { return Math.Max(1, (Width - PadX * 2 + GapX) / (CardWidth + GapX)); } }
        int ContentHeight { get { int rows = (items.Count + Columns - 1) / Columns; return HeaderHeight + rows * (CardHeight + GapY) + Ui.S(8); } }
        int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

        public LibraryItem SelectedItem { get { return selected >= 0 && selected < items.Count ? items[selected] : null; } }

        public void SetItems(IList<LibraryItem> source, int selectIndex)
        {
            DisposeCards();
            items.Clear();
            items.AddRange(source);
            lift = new float[items.Count];
            liftVelocity = new float[items.Count];
            restCards = new Bitmap[items.Count];
            liftedCards = new Bitmap[items.Count];
            hover = -1;
            selected = items.Count == 0 ? -1 : Math.Max(0, Math.Min(selectIndex, items.Count - 1));
            JumpScroll(scrollTarget);
            if (selected >= 0) EnsureVisible(selected, false);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        void Select(int index)
        {
            if (index < 0 || index >= items.Count) return;
            if (index != selected)
            {
                InvalidateCard(selected);
                selected = index;
                InvalidateCard(selected);
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            EnsureVisible(index, true);
        }

        int ContentTop(int index) { return HeaderHeight + index / Columns * (CardHeight + GapY); }

        Rectangle CardRect(int index)
        {
            int column = index % Columns;
            return new Rectangle(PadX + column * (CardWidth + GapX), ContentTop(index) - scroll, CardWidth, CardHeight);
        }

        void InvalidateCard(int index)
        {
            if (index < 0 || index >= items.Count) return;
            var rect = CardRect(index);
            rect.Inflate(GlowMargin + Ui.S(8), GlowMargin + Ui.S(16));
            Invalidate(rect);
        }

        void EnsureVisible(int index, bool animate)
        {
            if (Height <= 0) return;
            int top = ContentTop(index), bottom = top + CardHeight, margin = Ui.S(12);
            float target = scrollTarget;
            if (index < Columns) target = 0;
            else if (top - margin < target) target = top - margin;
            else if (bottom + margin > target + Height) target = bottom + margin - Height;
            if (animate) ScrollTo(target); else JumpScroll(target);
        }

        void ScrollTo(float value)
        {
            scrollTarget = Math.Max(0, Math.Min(value, MaxScroll));
            if (scrollTarget != scrollPos) StartAnimation();
        }

        void JumpScroll(float value)
        {
            scrollTarget = scrollPos = Math.Max(0, Math.Min(value, MaxScroll));
            scrollVelocity = 0;
            SetScrollPixels((int)Math.Round(scrollPos));
        }

        void SetScrollPixels(int value)
        {
            if (value == scroll) return;
            scroll = value;
            Invalidate();
            if (IsHandleCreated && !draggingThumb) UpdateHover(PointToClient(MousePosition));
        }

        int HitTest(Point point)
        {
            for (int i = 0; i < items.Count; i++)
                if (CardRect(i).Contains(point)) return i;
            return -1;
        }

        Rectangle ThumbRect()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            int track = Height - Ui.S(16);
            int thumb = Math.Max(Ui.S(40), (int)((long)track * Height / ContentHeight));
            int y = Ui.S(8) + (int)((long)(track - thumb) * Math.Min(scroll, MaxScroll) / MaxScroll);
            return new Rectangle(Width - Ui.S(12), y, Ui.S(6), thumb);
        }

        void UpdateHover(Point point)
        {
            var thumb = ThumbRect();
            thumb.Inflate(Ui.S(4), 0);
            bool overThumb = !thumb.IsEmpty && thumb.Contains(point);
            if (overThumb != thumbHover) { thumbHover = overThumb; Invalidate(new Rectangle(Width - Ui.S(20), 0, Ui.S(20), Height)); }
            int index = overThumb || !ClientRectangle.Contains(point) ? -1 : HitTest(point);
            if (index != hover)
            {
                hover = index;
                Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
                StartAnimation();
            }
        }

        // ----- Animation -----------------------------------------------------------------

        void StartAnimation()
        {
            if (animating) return;
            animating = true;
            lastTicks = clock.ElapsedTicks;
            FrameClock.Acquire();
            animation.Start();
        }

        void StopAnimation()
        {
            if (!animating) return;
            animating = false;
            animation.Stop();
            FrameClock.Release();
        }

        void StepAnimation()
        {
            long now = clock.ElapsedTicks;
            float dt = Math.Min(0.05f, (float)((now - lastTicks) / (double)Stopwatch.Frequency));
            lastTicks = now;
            if (dt <= 0f) return;
            bool moving = false;

            if (scrollPos != scrollTarget || scrollVelocity != 0f)
            {
                FrameClock.Spring(ref scrollPos, ref scrollVelocity, scrollTarget, ScrollOmega, dt);
                if (Math.Abs(scrollPos - scrollTarget) < 0.5f && Math.Abs(scrollVelocity) < 8f) { scrollPos = scrollTarget; scrollVelocity = 0f; }
                else moving = true;
                SetScrollPixels((int)Math.Round(scrollPos));
            }

            for (int i = 0; i < lift.Length; i++)
            {
                float target = i == hover ? 1f : 0f;
                if (lift[i] == target && liftVelocity[i] == 0f) continue;
                InvalidateCard(i);
                FrameClock.Spring(ref lift[i], ref liftVelocity[i], target, HoverOmega, dt);
                if (Math.Abs(lift[i] - target) < 0.002f && Math.Abs(liftVelocity[i]) < 0.02f) { lift[i] = target; liftVelocity[i] = 0f; }
                else moving = true;
            }
            if (!moving) StopAnimation();
        }

        // ----- Input ---------------------------------------------------------------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            JumpScroll(scrollTarget);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (draggingThumb)
            {
                var thumb = ThumbRect();
                int travel = Math.Max(1, Height - Ui.S(16) - thumb.Height);
                JumpScroll(dragOriginScroll + (float)(e.Y - dragOriginY) * MaxScroll / travel);
                return;
            }
            UpdateHover(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (draggingThumb) return;
            UpdateHover(new Point(-1, -1));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var thumb = ThumbRect();
            thumb.Inflate(Ui.S(4), 0);
            if (e.Button == MouseButtons.Left && !thumb.IsEmpty && thumb.Contains(e.Location))
            {
                draggingThumb = true;
                dragOriginY = e.Y;
                dragOriginScroll = scroll;
                return;
            }
            int index = HitTest(e.Location);
            if (index < 0) return;
            Select(index);
            if (e.Button == MouseButtons.Right && ItemMenuRequested != null)
                ItemMenuRequested(this, new ItemEventArgs { Item = items[index], ScreenLocation = PointToScreen(e.Location) });
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (draggingThumb) { draggingThumb = false; Invalidate(); UpdateHover(e.Location); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            int index = HitTest(e.Location);
            if (index >= 0 && ItemActivated != null) ItemActivated(this, new ItemEventArgs { Item = items[index] });
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(scrollTarget - e.Delta * Ui.S(150) / 120f);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (items.Count == 0) return;
            int columns = Columns, next = selected;
            switch (e.KeyCode)
            {
                case Keys.Left: next--; break;
                case Keys.Right: next++; break;
                case Keys.Up: next -= columns; break;
                case Keys.Down: next += columns; break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = items.Count - 1; break;
                case Keys.Enter:
                    if (SelectedItem != null && ItemActivated != null) ItemActivated(this, new ItemEventArgs { Item = SelectedItem });
                    e.Handled = true;
                    return;
                case Keys.Apps:
                    if (SelectedItem != null && ItemMenuRequested != null)
                    {
                        var rect = CardRect(selected);
                        ItemMenuRequested(this, new ItemEventArgs { Item = SelectedItem, ScreenLocation = PointToScreen(new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2)) });
                    }
                    e.Handled = true;
                    return;
                default:
                    return;
            }
            if (next >= 0 && next < items.Count) Select(next);
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); InvalidateCard(selected); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); InvalidateCard(selected); }

        // ----- Painting ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.HighQuality(g);
            // Gamma-corrected compositing is several times slower and invisible on these dark surfaces.
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            g.Clear(Ui.Background);
            // Fixed (non-scrolling) wash of the theme colour that fades into the background.
            int washHeight = Math.Max(1, Math.Min(Height, Math.Max(Ui.S(460), Height * 3 / 4)));
            using (var wash = new LinearGradientBrush(new Rectangle(0, -1, Math.Max(1, Width), washHeight + 2), Ui.LibraryTop, Ui.Background, 90f))
            {
                var blend = new Blend(3);
                blend.Factors = new[] { 0f, 0.55f, 1f };
                blend.Positions = new[] { 0f, 0.4f, 1f };
                wash.Blend = blend;
                g.FillRectangle(wash, 0, 0, Width, washHeight);
            }

            int ready = 0;
            foreach (var item in items) if (item.State == GameState.Ready) ready++;
            float headerY = Ui.F(10) - scroll;
            if (headerY > -Ui.F(60) || items.Count == 0)
            {
                using (var titleFont = new Font("Segoe UI", 15f, FontStyle.Bold))
                using (var metaFont = new Font("Segoe UI", 10f))
                using (var text = new SolidBrush(Ui.Text))
                using (var muted = new SolidBrush(Ui.Muted))
                {
                    var titleSize = g.MeasureString("Library", titleFont);
                    g.DrawString("Library", titleFont, text, PadX - Ui.F(3), headerY);
                    if (items.Count > 0)
                    {
                        string meta = ready + " of " + items.Count + " ready";
                        var metaSize = g.MeasureString(meta, metaFont);
                        g.DrawString(meta, metaFont, muted, PadX + titleSize.Width + Ui.F(4), headerY + (titleSize.Height - metaSize.Height) / 2f + Ui.F(2));
                    }
                    else
                    {
                        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString("No game profiles found.\nUse Import profile to add one.", metaFont, muted, ClientRectangle, center);
                        return;
                    }
                }
            }

            var lifted = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                if (lift[i] > 0.0005f) lifted.Add(i);
                else DrawCard(g, i, 0f, e.ClipRectangle);
            }
            lifted.Sort(delegate(int a, int b) { return lift[a].CompareTo(lift[b]); });
            foreach (int i in lifted) DrawCard(g, i, lift[i], e.ClipRectangle);

            var thumb = ThumbRect();
            if (!thumb.IsEmpty)
                using (var path = Ui.Rounded(thumb, thumb.Width / 2f))
                using (var brush = new SolidBrush(Color.FromArgb(thumbHover || draggingThumb ? 110 : 50, 255, 255, 255)))
                    g.FillPath(brush, path);
        }

        void DrawCard(Graphics g, int index, float t, Rectangle clip)
        {
            var rect = CardRect(index);
            int margin = GlowMargin + Ui.S(16);
            if (rect.Bottom + margin < clip.Top || rect.Top - margin > clip.Bottom ||
                rect.Right + margin < clip.Left || rect.Left - margin > clip.Right) return;
            var item = items[index];
            bool isSelected = index == selected;

            // Lifted cards scale and rise with sub-pixel precision; resting cards are blitted 1:1.
            float scale = 1f + LiftScale * t;
            float w = rect.Width * scale, h = rect.Height * scale;
            var card = t > 0f
                ? new RectangleF(rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f - Ui.F(8) * t, w, h)
                : new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
            float radius = Ui.F(12) * scale;

            if (t > 0f)
            {
                DrawGlow(g, index, card, t);
                var bitmap = CardBitmap(index, true);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(bitmap, card, new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            }
            else
            {
                var bitmap = CardBitmap(index, false);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(bitmap, new Rectangle(rect.X, rect.Y, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            }

            using (var path = Ui.Rounded(card, radius))
            using (var border = new Pen(Color.FromArgb((int)(26 + 64 * t), Ui.Mix(Color.White, item.Accent, 0.45f * t)), Math.Max(1f, Ui.Scale)))
                g.DrawPath(border, path);

            if (isSelected)
            {
                var ring = card;
                float inset = Ui.F(4);
                ring.Inflate(inset, inset);
                Color ringColor = Ui.Mix(item.Accent, Color.White, 0.15f);
                if (!Focused) ringColor = Color.FromArgb(190, ringColor);
                using (var path = Ui.Rounded(ring, radius + inset))
                using (var pen = new Pen(ringColor, Ui.F(2.5f)))
                    g.DrawPath(pen, path);
            }
        }

        // Drop shadow plus a glow in the cover's colour. Colour-matrix blending is far too slow to run
        // every frame, so each hovered card's glow is baked once at GlowSteps opacities and then blitted.
        const int GlowSteps = 12;
        sealed class GlowSet { public int Index; public Bitmap[] Levels; public int DropOffset; }
        readonly List<GlowSet> glowSets = new List<GlowSet>();

        void DrawGlow(Graphics g, int index, RectangleF card, float t)
        {
            int level = (int)Math.Round(Math.Min(1f, t) * GlowSteps);
            if (level <= 0) return;
            var set = GlowFor(index);
            var bitmap = set.Levels[level - 1];
            float cx = card.X + card.Width / 2f, cy = card.Y + card.Height / 2f;
            int x = (int)Math.Round(cx - bitmap.Width / 2f), y = (int)Math.Round(cy - (bitmap.Height - set.DropOffset) / 2f);
            var previous = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(bitmap, new Rectangle(x, y, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
            g.InterpolationMode = previous;
        }

        GlowSet GlowFor(int index)
        {
            for (int i = 0; i < glowSets.Count; i++)
                if (glowSets[i].Index == index)
                {
                    var found = glowSets[i];
                    glowSets.RemoveAt(i);
                    glowSets.Insert(0, found);
                    return found;
                }
            var set = BuildGlow(index);
            glowSets.Insert(0, set);
            while (glowSets.Count > 3)
            {
                foreach (var bitmap in glowSets[glowSets.Count - 1].Levels) bitmap.Dispose();
                glowSets.RemoveAt(glowSets.Count - 1);
            }
            return set;
        }

        GlowSet BuildGlow(int index)
        {
            float s = 1f + LiftScale, m = GlowMargin * s;
            float w = CardWidth * s + m * 2f, h = CardHeight * s + m * 2f;
            int drop = Ui.S(12);
            int bw = (int)Math.Ceiling(w), bh = (int)Math.Ceiling(h) + drop;
            var levels = new Bitmap[GlowSteps];
            using (var full = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(full))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    TintMask(g, new RectangleF(0, drop, w, h), Color.Black, 0.75f);
                    TintMask(g, new RectangleF(0, 0, w, h), Ui.Mix(items[index].Accent, Color.White, 0.15f), 0.9f);
                }
                var area = new Rectangle(0, 0, bw, bh);
                var data = full.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                var source = new byte[data.Stride * bh];
                Marshal.Copy(data.Scan0, source, 0, source.Length);
                int stride = data.Stride;
                full.UnlockBits(data);
                var scaled = new byte[source.Length];
                for (int level = 1; level <= GlowSteps; level++)
                {
                    // Premultiplied pixels fade correctly by scaling every channel.
                    int factor = level * 256 / GlowSteps;
                    for (int i = 0; i < source.Length; i++) scaled[i] = (byte)((source[i] * factor) >> 8);
                    var bitmap = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
                    var target = bitmap.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    if (target.Stride == stride) Marshal.Copy(scaled, 0, target.Scan0, scaled.Length);
                    bitmap.UnlockBits(target);
                    levels[level - 1] = bitmap;
                }
            }
            return new GlowSet { Index = index, Levels = levels, DropOffset = drop };
        }

        void TintMask(Graphics g, RectangleF dest, Color color, float opacity)
        {
            var mask = GlowMask();
            var matrix = new ColorMatrix(new[] {
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { 0f, 0f, 0f, Math.Min(1f, opacity), 0f },
                new[] { color.R / 255f, color.G / 255f, color.B / 255f, 0f, 1f }
            });
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(matrix);
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.Bilinear;
                var corners = new[] { dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) };
                g.DrawImage(mask, corners, new RectangleF(0, 0, mask.Width, mask.Height), GraphicsUnit.Pixel, attributes);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            }
        }

        Bitmap GlowMask()
        {
            if (glowMask != null) return glowMask;
            int m = GlowMargin, w = CardWidth + m * 2, h = CardHeight + m * 2;
            const int factor = 10;
            using (var small = new Bitmap(Math.Max(1, w / factor), Math.Max(1, h / factor), PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float s = 1f / factor;
                    var body = new RectangleF((m - Ui.F(4)) * s, (m - Ui.F(4)) * s, (CardWidth + Ui.F(8)) * s, (CardHeight + Ui.F(8)) * s);
                    using (var path = Ui.Rounded(body, Ui.F(14) * s))
                    using (var brush = new SolidBrush(Color.White))
                        g.FillPath(brush, path);
                }
                glowMask = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(glowMask))
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(small, new Rectangle(0, 0, w, h), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, attributes);
                }
            }
            return glowMask;
        }

        Bitmap CardBitmap(int index, bool lifted)
        {
            var cache = lifted ? liftedCards : restCards;
            if (cache[index] == null) cache[index] = RenderCard(items[index], lifted ? 1f + LiftScale : 1f);
            return cache[index];
        }

        // Poster, fade and caption composited once per card at the exact size it will be shown.
        Bitmap RenderCard(LibraryItem item, float scale)
        {
            int w = (int)Math.Round(CardWidth * scale), h = (int)Math.Round(CardHeight * scale);
            int posterH = (int)Math.Round(PosterHeight * scale);
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                Ui.HighQuality(g);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                var bounds = new RectangleF(0, 0, w, h);
                Color caption = CaptionColorProvider != null ? CaptionColorProvider(item) : Ui.GreyCaption;
                using (var path = Ui.Rounded(bounds, Ui.F(12) * scale))
                {
                    using (var brush = new SolidBrush(caption)) g.FillPath(brush, path);
                    var poster = PosterProvider != null ? PosterProvider(item, w, posterH) : null;
                    if (poster != null)
                        using (var texture = new TextureBrush(poster, WrapMode.Clamp))
                        {
                            texture.ScaleTransform(w / (float)poster.Width, posterH / (float)poster.Height);
                            g.FillPath(texture, path);
                        }
                }

                var captionRect = new RectangleF(0, posterH, w, h - posterH);
                string label = Ui.StateCaption(item);
                using (var font = new Font("Segoe UI", 9.5f * 96f / 72f * Ui.Scale * scale, GraphicsUnit.Pixel))
                using (var textBrush = new SolidBrush(item.State == GameState.Ready ? Ui.Muted : Ui.Dim))
                using (var dotBrush = new SolidBrush(Ui.StateColor(item)))
                {
                    var size = g.MeasureString(label, font);
                    float dot = Ui.F(7) * scale, gap = Ui.F(7) * scale;
                    float x = captionRect.X + (captionRect.Width - (dot + gap + size.Width)) / 2f;
                    float cy = captionRect.Y + captionRect.Height / 2f - Ui.F(2) * scale;
                    g.FillEllipse(dotBrush, x, cy - dot / 2f, dot, dot);
                    g.DrawString(label, font, textBrush, x + dot + gap, cy - size.Height / 2f);
                }
            }
            return bitmap;
        }

        void DisposeCards()
        {
            foreach (var cache in new[] { restCards, liftedCards })
                for (int i = 0; i < cache.Length; i++)
                    if (cache[i] != null) { cache[i].Dispose(); cache[i] = null; }
            foreach (var set in glowSets)
                foreach (var bitmap in set.Levels) bitmap.Dispose();
            glowSets.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopAnimation();
                animation.Dispose();
                DisposeCards();
                if (glowMask != null) glowMask.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    sealed class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(28, 31, 38);
        static readonly Color Highlight = Color.FromArgb(50, 55, 66);
        static readonly Color Border = Color.FromArgb(56, 62, 74);
        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemBorder { get { return Highlight; } }
        public override Color MenuItemSelected { get { return Highlight; } }
        public override Color MenuItemSelectedGradientBegin { get { return Highlight; } }
        public override Color MenuItemSelectedGradientEnd { get { return Highlight; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return Back; } }
    }

    public sealed partial class MainForm : Form
    {
        static readonly string[] CoverExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

        readonly string baseDirectory;
        readonly string profilesDirectory;
        readonly string dataDirectory;
        readonly string locationsFile;
        readonly string coversDirectory;
        readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        readonly List<LibraryItem> items = new List<LibraryItem>();
        readonly Dictionary<string, Bitmap> posterCache = new Dictionary<string, Bitmap>();
        readonly Dictionary<string, Image> customCovers = new Dictionary<string, Image>();
        Dictionary<string, string> profileLocations = new Dictionary<string, string>();

        TopBar topBar;
        HeroPanel hero;
        LibraryView library;
        ContextMenuStrip menu;

        public MainForm()
        {
            baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            profilesDirectory = Path.Combine(baseDirectory, "profiles");
            // CROSSCREASE_DATA_DIRECTORY is for automated tests and portable developer setups only.
            string dataOverride = Environment.GetEnvironmentVariable("CROSSCREASE_DATA_DIRECTORY");
            dataDirectory = !string.IsNullOrWhiteSpace(dataOverride) ? Path.GetFullPath(dataOverride)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrossCrease");
            locationsFile = Path.Combine(dataDirectory, "locations.json");
            coversDirectory = Path.Combine(dataDirectory, "covers");
            packages = new PackageStore(dataDirectory);
            LoadLocations();
            LoadFeedOverrides();
            CleanUpPreviousLauncher();

            Text = "CrossCrease";
            AutoScaleMode = AutoScaleMode.None;
            var workArea = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Ui.S(1240), workArea.Width * 9 / 10), Math.Min(Ui.S(820), workArea.Height * 9 / 10));
            MinimumSize = new Size(Math.Min(Ui.S(900), workArea.Width), Math.Min(Ui.S(640), workArea.Height));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Background;
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            themeTimer.Tick += delegate { StepTheme(); };
            BuildInterface();
            LoadProfiles(null);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
            ApplyCaptionColor();
        }

        void ApplyCaptionColor()
        {
            if (!IsHandleCreated) return;
            try
            {
                int caption = ColorTranslator.ToWin32(Ui.TopBarColor);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR (Windows 11)
            }
            catch { }
        }

        // ----- Theme colour --------------------------------------------------------------

        readonly Timer themeTimer = new Timer { Interval = 8 };
        readonly Stopwatch themeClock = new Stopwatch();
        Color themeFrom, themeTo;
        const double ThemeSeconds = 0.38;

        // Every game uses the colour of its full-colour cover, even when the card itself is greyed out.
        Color ThemeFor(LibraryItem item)
        {
            return item == null ? Ui.Brand : item.Accent;
        }

        void SetTheme(Color target, bool animate)
        {
            if (themeTimer.Enabled) { themeTimer.Stop(); FrameClock.Release(); }
            if (!animate || !Visible)
            {
                Ui.Theme = target;
                RepaintTheme();
                return;
            }
            themeFrom = Ui.Theme;
            themeTo = target;
            themeClock.Restart();
            FrameClock.Acquire();
            themeTimer.Start();
        }

        void StepTheme()
        {
            float t = (float)Math.Min(1.0, themeClock.Elapsed.TotalSeconds / ThemeSeconds);
            float eased = t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f; // ease in-out
            Ui.Theme = Ui.Mix(themeFrom, themeTo, eased);
            RepaintTheme();
            if (t >= 1f) { themeTimer.Stop(); FrameClock.Release(); }
        }

        void RepaintTheme()
        {
            topBar.Invalidate(true);
            hero.SetContent();
            library.Invalidate();
            ApplyCaptionColor();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            library.Focus();
            BeginUpdateCheck();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5) { e.Handled = true; ReloadProfiles(); }
        }

        void BuildInterface()
        {
            library = new LibraryView { Dock = DockStyle.Fill, TabIndex = 0 };
            library.PosterProvider = CardPoster;
            library.CaptionColorProvider = CaptionColor;
            library.SelectionChanged += delegate { UpdateHero(); };
            library.ItemActivated += delegate(object sender, ItemEventArgs args) { ActivateItem(args.Item); };
            library.ItemMenuRequested += delegate(object sender, ItemEventArgs args) { ShowItemMenu(args.Item, args.ScreenLocation); };

            hero = new HeroPanel { Dock = DockStyle.Top, TabIndex = 1 };
            hero.PrimaryButton.Click += delegate { ActivateItem(library.SelectedItem); };
            hero.SettingsButton.Click += delegate { ShowGameSettings(library.SelectedItem); };
            hero.FolderButton.Click += delegate { OpenGameFolder(library.SelectedItem); };

            topBar = new TopBar { Dock = DockStyle.Top, TabIndex = 2 };
            topBar.ImportButton.Click += delegate { ImportProfile(); };
            topBar.RefreshButton.Click += delegate { ReloadProfiles(); };
            topBar.FolderButton.Click += delegate { OpenProfilesFolder(); };
            topBar.UpdateButton.Click += delegate { UpdateLauncher(); };

            Controls.Add(library);
            Controls.Add(hero);
            Controls.Add(topBar);

            menu = new ContextMenuStrip {
                Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
                ShowImageMargin = false, BackColor = Color.FromArgb(28, 31, 38), ForeColor = Ui.Text,
                Font = new Font("Segoe UI", 10f)
            };
        }

        // ----- Library state -----------------------------------------------------------------

        void ReloadProfiles()
        {
            var current = library.SelectedItem;
            LoadProfiles(current == null ? null : current.Profile.id);
            BeginUpdateCheck();
        }

        void LoadProfiles(string preferredId)
        {
            Directory.CreateDirectory(profilesDirectory);
            ClearArtwork();
            items.Clear();

            var profiles = new List<GameProfile>();
            foreach (var file in Directory.GetFiles(profilesDirectory, "*.json"))
            {
                try
                {
                    var profile = serializer.Deserialize<GameProfile>(File.ReadAllText(file));
                    if (!ProfileUsable(profile)) continue;
                    profile.SourceFile = file;
                    if (string.IsNullOrWhiteSpace(profile.id)) profile.id = Path.GetFileNameWithoutExtension(file);
                    profiles.Add(profile);
                }
                catch { }
            }
            profiles.Sort(delegate(GameProfile left, GameProfile right) { return NaturalCompare(left.name, right.name); });

            foreach (var profile in profiles) items.Add(BuildItem(profile));

            int index = -1;
            if (!string.IsNullOrWhiteSpace(preferredId)) index = items.FindIndex(delegate(LibraryItem item) { return item.Profile.id == preferredId; });
            if (index < 0) index = items.FindIndex(delegate(LibraryItem item) { return item.State == GameState.Ready; });
            if (index < 0) index = 0;
            library.SetItems(items, index);
        }

        static bool IsPlanned(GameProfile profile)
        {
            return string.Equals(profile.status, "planned", StringComparison.OrdinalIgnoreCase);
        }

        static bool ProfileUsable(GameProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.name)) return false;
            return IsPlanned(profile) || !string.IsNullOrWhiteSpace(profile.executable) || !string.IsNullOrWhiteSpace(profile.executable_relative);
        }

        LibraryItem BuildItem(GameProfile profile)
        {
            var item = new LibraryItem { Profile = profile, Accent = CoverColor(profile) };
            if (IsPlanned(profile))
            {
                item.State = GameState.Planned;
                return item;
            }
            item.Root = ProfileRoot(profile);
            item.Executable = ProfileExecutable(profile);
            item.GameFound = ProfileAvailable(profile);
            ApplyPackageState(item);
            if (item.MissingPackages.Count > 0) item.State = GameState.NeedsDownload;
            else item.State = item.GameFound ? GameState.Ready : GameState.Missing;
            return item;
        }

        // Accent taken from the cover image when one exists, otherwise the profile's accent.
        Color CoverColor(GameProfile profile)
        {
            var cover = CustomCover(profile);
            if (cover != null)
            {
                try
                {
                    var dominant = Ui.Dominant(cover);
                    if (dominant.HasValue) return dominant.Value;
                }
                catch { }
            }
            return ProfileColor(profile);
        }

        void UpdateHero()
        {
            var item = library.SelectedItem;
            SetTheme(ThemeFor(item), true);
            hero.Item = item;
            var primary = hero.PrimaryButton;
            primary.ShowPlayIcon = false;
            primary.Accent = Ui.Brand;
            if (item == null)
            {
                hero.Poster = null;
                hero.Detail = "";
                primary.Text = "Import profile";
                primary.Enabled = true;
                hero.SetButtons();
                hero.SetContent();
                return;
            }

            var size = hero.PosterSize;
            hero.Poster = Poster(item, size.Width, size.Height, false);
            bool busy = IsInstalling(item);
            hero.Progress = busy ? installing.Fraction : -1f;
            if (busy)
            {
                primary.Text = installing.Phase;
                primary.Enabled = false;
                hero.Detail = installing.Detail;
                hero.SetButtons(primary);
                hero.SetContent();
                return;
            }
            switch (item.State)
            {
                case GameState.Ready:
                    primary.Text = "Play";
                    primary.ShowPlayIcon = true;
                    primary.Accent = item.Accent;
                    primary.Enabled = true;
                    hero.Detail = string.IsNullOrWhiteSpace(item.Root) ? item.Executable : item.Root;
                    string version = GameVersionSummary(item.Root);
                    if (version != null) hero.Detail = version + " · " + hero.Detail;
                    var buttons = new List<FlatButton> { primary };
                    buttons.AddRange(LaunchOptionButtons(item));
                    var update = UpdateButton(item);
                    if (update != null) { buttons.Add(update); hero.Detail = UpdateSummary(item); }
                    buttons.Add(hero.SettingsButton);
                    hero.SetButtons(buttons.ToArray());
                    break;
                case GameState.NeedsDownload:
                    ShowDownloadHero(item);
                    break;
                case GameState.Missing:
                    primary.Text = "Locate game";
                    primary.Enabled = true;
                    hero.Detail = GameFileRelative(item.Profile) != null
                        ? "Select the game's " + Path.GetFileName(GameFileRelative(item.Profile)) + " or its folder once."
                        : "Select the game's folder once.";
                    hero.SetButtons(primary, hero.SettingsButton);
                    break;
                default:
                    primary.Text = "Coming soon";
                    primary.Enabled = false;
                    hero.Detail = "Planned for a future CrossCrease release.";
                    hero.SetButtons(primary);
                    break;
            }
            hero.SetContent();
        }

        void ActivateItem(LibraryItem item)
        {
            if (item == null) { ImportProfile(); return; }
            if (item.State == GameState.Ready) LaunchGame(item, null);
            else if (item.State == GameState.Missing) LocateGame(item);
            else if (item.State == GameState.NeedsDownload) DownloadButtonClicked(item);
        }

        void ShowItemMenu(LibraryItem item, Point screen)
        {
            menu.Items.Clear();
            if (item.State != GameState.Planned)
            {
                var settings = AddMenuItem("Game settings…", delegate { ShowGameSettings(item); });
                settings.Font = new Font(menu.Font, FontStyle.Bold);
            }
            if (item.State == GameState.Ready)
            {
                var play = AddMenuItem("Play " + item.Profile.name, delegate { LaunchGame(item, null); });
                play.Font = new Font(menu.Font, FontStyle.Bold);
            }
            if (item.State != GameState.Planned)
            {
                var files = new ToolStripMenuItem(item.GameFound ? "Game files" : "Locate game") { ForeColor = Ui.Text, Padding = new Padding(4, 3, 4, 3) };
                files.DropDown.BackColor = menu.BackColor;
                ((ToolStripDropDownMenu)files.DropDown).ShowImageMargin = false;
                files.DropDownItems.AddRange(GameFilesMenuItems(item).ToArray());
                menu.Items.Add(files);
                AddMenuItem("Open game folder", delegate { OpenGameFolder(item); }).Enabled = Directory.Exists(GameFolder(item));
                AddPackageMenuItems(item);
                menu.Items.Add(new ToolStripSeparator());
            }
            AddMenuItem("Choose cover image…", delegate { ChooseCover(item); });
            AddMenuItem("Reset cover image", delegate { ResetCover(item); }).Enabled = UserCoverPath(item.Profile) != null;
            menu.Show(screen);
        }

        ToolStripMenuItem AddMenuItem(string text, EventHandler handler)
        {
            var entry = new ToolStripMenuItem(text) { ForeColor = Ui.Text, Padding = new Padding(4, 3, 4, 3) };
            entry.Click += handler;
            menu.Items.Add(entry);
            return entry;
        }

        // ----- Artwork ---------------------------------------------------------------------

        Color CaptionColor(LibraryItem item)
        {
            return item.State == GameState.Ready ? Ui.Mix(item.Accent, Ui.Background, 0.84f) : Ui.GreyCaption;
        }

        Bitmap CardPoster(LibraryItem item, int width, int height)
        {
            return Poster(item, width, height, true);
        }

        Bitmap Poster(LibraryItem item, int width, int height, bool fade)
        {
            string key = item.Profile.id + "|" + width + "x" + height + "|" + item.State + "|" + fade;
            Bitmap bitmap;
            if (posterCache.TryGetValue(key, out bitmap)) return bitmap;
            Color? fadeTo = null;
            if (fade) fadeTo = CaptionColor(item);
            bitmap = CoverArt.Render(item, CustomCover(item.Profile), width, height, item.State != GameState.Ready, fadeTo);
            posterCache[key] = bitmap;
            return bitmap;
        }

        Image CustomCover(GameProfile profile)
        {
            Image image;
            if (customCovers.TryGetValue(profile.id, out image)) return image;
            image = null;
            string path = UserCoverPath(profile) ?? DownloadedCoverPath(profile) ?? ProfileCoverPath(profile);
            if (path != null)
            {
                try
                {
                    using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                    using (var loaded = Image.FromStream(stream))
                        image = new Bitmap(loaded);
                }
                catch { image = null; }
            }
            customCovers[profile.id] = image;
            return image;
        }

        string UserCoverPath(GameProfile profile)
        {
            foreach (var extension in CoverExtensions)
            {
                string candidate = Path.Combine(coversDirectory, SafeFileName(profile.id) + extension);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        string ProfileCoverPath(GameProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.cover)) return null;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(profile.cover.Replace("{launcher}", baseDirectory.TrimEnd(Path.DirectorySeparatorChar)));
                string folder = string.IsNullOrWhiteSpace(profile.SourceFile) ? profilesDirectory : Path.GetDirectoryName(profile.SourceFile);
                string full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(folder, expanded));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        static string SafeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value;
        }

        void ClearArtwork()
        {
            foreach (var bitmap in posterCache.Values) bitmap.Dispose();
            posterCache.Clear();
            foreach (var image in customCovers.Values) if (image != null) image.Dispose();
            customCovers.Clear();
        }

        void ChooseCover(LibraryItem item)
        {
            using (var dialog = new OpenFileDialog {
                Title = "Choose a cover image for " + item.Profile.name,
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
            })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                try
                {
                    using (var stream = new MemoryStream(File.ReadAllBytes(dialog.FileName)))
                    using (Image.FromStream(stream)) { }
                    Directory.CreateDirectory(coversDirectory);
                    ClearArtwork();
                    RemoveUserCovers(item.Profile);
                    string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    File.Copy(dialog.FileName, Path.Combine(coversDirectory, SafeFileName(item.Profile.id) + extension), true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(DialogOwner, ex.Message, "Could not use that image", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                LoadProfiles(item.Profile.id);
            }
        }

        void ResetCover(LibraryItem item)
        {
            ClearArtwork();
            try { RemoveUserCovers(item.Profile); }
            catch (Exception ex) { MessageBox.Show(DialogOwner, ex.Message, "Could not reset cover", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            LoadProfiles(item.Profile.id);
            BeginCoverSync(false);
        }

        void RemoveUserCovers(GameProfile profile)
        {
            foreach (var extension in CoverExtensions)
            {
                string existing = Path.Combine(coversDirectory, SafeFileName(profile.id) + extension);
                if (File.Exists(existing)) File.Delete(existing);
            }
        }

        // ----- Profile resolution ----------------------------------------------------------

        // Paths may use environment variables, {launcher}, {data}, {package:<id>} and {setting:<id>}.
        // A path that refers to a package that is not installed resolves to "".
        string ResolvePath(GameProfile profile, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string expanded = ExpandTokens(profile, value);
            if (expanded == null) return "";
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(expanded);
                return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : Path.GetFullPath(Path.Combine(baseDirectory, expanded));
            }
            catch { return ""; }
        }

        string ProfileRoot(GameProfile profile)
        {
            string saved;
            // A saved location is used only while it still matches the profile, so a profile whose layout
            // changed falls back to discovery instead of pointing at an outdated folder.
            if (profileLocations.TryGetValue(profile.id, out saved) && ProfileValidAtRoot(profile, saved)) return saved;
            if (profile.search_roots != null)
            {
                foreach (var candidateValue in profile.search_roots)
                {
                    string candidate = ResolvePath(profile, candidateValue);
                    if (ProfileValidAtRoot(profile, candidate)) return candidate;
                }
            }
            return "";
        }

        string ProfileExecutable(GameProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.executable_relative))
            {
                string root = ProfileRoot(profile);
                return string.IsNullOrWhiteSpace(root) ? "" : Path.GetFullPath(Path.Combine(root, profile.executable_relative));
            }
            return ResolvePath(profile, profile.executable);
        }

        string ProfileRequiredFile(GameProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.required_file_relative))
            {
                string root = ProfileRoot(profile);
                return string.IsNullOrWhiteSpace(root) ? "" : Path.GetFullPath(Path.Combine(root, profile.required_file_relative));
            }
            string required = ResolvePath(profile, profile.required_file);
            return string.IsNullOrWhiteSpace(required) ? ProfileExecutable(profile) : required;
        }

        bool ProfileValidAtRoot(GameProfile profile, string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
            string executable = string.IsNullOrWhiteSpace(profile.executable_relative)
                ? ResolvePath(profile, profile.executable) : Path.GetFullPath(Path.Combine(root, profile.executable_relative));
            string required = string.IsNullOrWhiteSpace(profile.required_file_relative)
                ? ResolvePath(profile, profile.required_file) : Path.GetFullPath(Path.Combine(root, profile.required_file_relative));
            if (string.IsNullOrWhiteSpace(required)) required = executable;
            return File.Exists(executable) && File.Exists(required);
        }

        bool ProfileAvailable(GameProfile profile)
        {
            if (IsPlanned(profile)) return false;
            string executable = ProfileExecutable(profile);
            string required = ProfileRequiredFile(profile);
            return File.Exists(executable) && File.Exists(required);
        }

        Color ProfileColor(GameProfile profile)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(profile.accent)) return ColorTranslator.FromHtml(profile.accent);
            }
            catch { }
            return Ui.Brand;
        }

        public static string ProfileToken(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "CC";
            var pieces = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = pieces.Length - 1; i >= 0; i--)
            {
                int value;
                if (int.TryParse(pieces[i], out value)) return pieces[i];
            }
            return name.Length <= 2 ? name.ToUpperInvariant() : name.Substring(0, 2).ToUpperInvariant();
        }

        static int NaturalCompare(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int numeric = string.CompareOrdinal(na, nb);
                    if (numeric != 0) return numeric;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        string ResolveWorkingDirectory(GameProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.working_directory_relative))
            {
                string root = ProfileRoot(profile);
                if (!string.IsNullOrWhiteSpace(root)) return Path.GetFullPath(Path.Combine(root, profile.working_directory_relative));
            }
            string configured = ResolvePath(profile, profile.working_directory);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            string executable = ProfileExecutable(profile);
            return string.IsNullOrWhiteSpace(executable) ? "" : Path.GetDirectoryName(executable);
        }

        string ProfileArguments(GameProfile profile, string template)
        {
            if (template == null)
                template = string.IsNullOrWhiteSpace(profile.arguments_template) ? (profile.arguments ?? "") : profile.arguments_template;
            string root = ProfileRoot(profile);
            string executable = ProfileExecutable(profile);
            return Environment.ExpandEnvironmentVariables(ExpandTokens(profile, template) ?? "")
                .Replace("{root}", root)
                .Replace("{required_file}", ProfileRequiredFile(profile))
                .Replace("{executable_dir}", string.IsNullOrWhiteSpace(executable) ? "" : Path.GetDirectoryName(executable));
        }

        // ----- Actions ---------------------------------------------------------------------

        void LaunchGame(LibraryItem item, LaunchOption option)
        {
            if (item == null || item.State != GameState.Ready || installing != null) return;
            var profile = item.Profile;
            if (option != null && !PrepareLaunchOption(item, option)) return;
            string executable = option != null && !string.IsNullOrWhiteSpace(option.executable)
                ? ResolvePath(profile, option.executable) : ProfileExecutable(profile);
            if (!File.Exists(executable))
            {
                LoadProfiles(profile.id);
                return;
            }
            string workingDirectory = option != null && !string.IsNullOrWhiteSpace(option.working_directory)
                ? ResolvePath(profile, option.working_directory) : ResolveWorkingDirectory(profile);
            try
            {
                if (!string.IsNullOrWhiteSpace(workingDirectory)) Directory.CreateDirectory(workingDirectory);
                Process.Start(new ProcessStartInfo(executable) {
                    Arguments = ProfileArguments(profile, option != null ? option.arguments_template : null),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false
                });
                WindowState = FormWindowState.Minimized;
            }
            catch (Exception ex)
            {
                MessageBox.Show(DialogOwner, ex.Message, "Could not start " + profile.name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ImportProfile()
        {
            using (var dialog = new OpenFileDialog { Title = "Import a CrossCrease game profile", Filter = "JSON profile (*.json)|*.json" })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                try
                {
                    var profile = serializer.Deserialize<GameProfile>(File.ReadAllText(dialog.FileName));
                    if (!ProfileUsable(profile))
                        throw new InvalidDataException("The profile needs a name and an executable or executable_relative field (or \"status\": \"planned\").");
                    Directory.CreateDirectory(profilesDirectory);
                    string destination = Path.Combine(profilesDirectory, Path.GetFileName(dialog.FileName));
                    if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                        File.Copy(dialog.FileName, destination, true);
                    LoadProfiles(string.IsNullOrWhiteSpace(profile.id) ? Path.GetFileNameWithoutExtension(destination) : profile.id);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(DialogOwner, ex.Message, "Could not import profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        void OpenGameFolder(LibraryItem item)
        {
            if (item == null) return;
            string folder = GameFolder(item);
            if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        void LoadLocations()
        {
            try
            {
                if (File.Exists(locationsFile))
                    profileLocations = serializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(locationsFile));
            }
            catch { profileLocations = new Dictionary<string, string>(); }
            if (profileLocations == null) profileLocations = new Dictionary<string, string>();
        }

        void SaveLocations()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(locationsFile));
            File.WriteAllText(locationsFile, serializer.Serialize(profileLocations));
        }

        void OpenProfilesFolder()
        {
            Directory.CreateDirectory(profilesDirectory);
            Process.Start("explorer.exe", "\"" + profilesDirectory + "\"");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClearArtwork();
            base.Dispose(disposing);
        }

        [STAThread]
        public static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = Math.Max(1f, g.DpiX / 96f);
            Application.Run(new MainForm());
        }
    }
}
