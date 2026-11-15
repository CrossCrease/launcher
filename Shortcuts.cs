using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CrossCrease
{
    // Shortcuts that start a game straight from the desktop or the Start menu ("CrossCrease.exe --play <id>").
    // Each shortcut's icon is the game's cover, written as a multi-size .ico in %LOCALAPPDATA%\CrossCrease\shortcuts.
    // The shortcuts CrossCrease made are remembered in shortcuts\shortcuts.json, so their icons follow the
    // cover when it changes (your own image, a downloaded cover, or a reset).
    public sealed partial class MainForm
    {
        public const string PlayArgument = "--play";
        static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

        sealed class ShortcutRecord
        {
            public string icon { get; set; }
            public string cover { get; set; }
            public List<string> links { get; set; }
        }

        string ShortcutsDirectory { get { return Path.Combine(dataDirectory, "shortcuts"); } }
        string ShortcutsFile { get { return Path.Combine(ShortcutsDirectory, "shortcuts.json"); } }

        Dictionary<string, ShortcutRecord> LoadShortcutRecords()
        {
            try
            {
                if (File.Exists(ShortcutsFile))
                {
                    var records = serializer.Deserialize<Dictionary<string, ShortcutRecord>>(File.ReadAllText(ShortcutsFile));
                    if (records != null) return new Dictionary<string, ShortcutRecord>(records, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return new Dictionary<string, ShortcutRecord>(StringComparer.OrdinalIgnoreCase);
        }

        void SaveShortcutRecords(Dictionary<string, ShortcutRecord> records)
        {
            Directory.CreateDirectory(ShortcutsDirectory);
            File.WriteAllText(ShortcutsFile, serializer.Serialize(records));
        }

        // The shortcuts made for this game that still exist (the player may have deleted or moved some).
        List<string> ExistingShortcuts(GameProfile profile)
        {
            var found = new List<string>();
            ShortcutRecord record;
            if (LoadShortcutRecords().TryGetValue(profile.id, out record) && record.links != null)
                foreach (var link in record.links)
                    if (!string.IsNullOrWhiteSpace(link) && File.Exists(link)) found.Add(link);
            return found;
        }

        bool HasShortcutIn(GameProfile profile, string folder)
        {
            return ExistingShortcuts(profile).Exists(delegate(string link) {
                return string.Equals(Path.GetDirectoryName(link), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            });
        }

        // Folders the shortcuts go into. CROSSCREASE_SHORTCUT_DIRECTORY (automated tests only) replaces both.
        static string DesktopShortcutFolder()
        {
            string test = Environment.GetEnvironmentVariable("CROSSCREASE_SHORTCUT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(test)) return Path.Combine(Path.GetFullPath(test), "Desktop");
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        static string StartMenuShortcutFolder()
        {
            string test = Environment.GetEnvironmentVariable("CROSSCREASE_SHORTCUT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(test)) return Path.Combine(Path.GetFullPath(test), "Start Menu", "CrossCrease");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CrossCrease");
        }

        // Called after the game was installed or its files were chosen: offers a shortcut once it can be played.
        void OfferShortcut(string profileId)
        {
            var item = ItemById(profileId);
            if (item == null || item.State != GameState.Ready || installing != null) return;
            if (ExistingShortcuts(item.Profile).Count > 0) return;
            ShowShortcutDialog(item, true);
        }

        // From the cover's right-click menu and Game settings, at any time.
        void ShowShortcutDialog(LibraryItem item, bool offer)
        {
            if (item == null || item.State == GameState.Planned) return;
            using (var icon = RenderShortcutIcon(item, Ui.S(64)))
            using (var dialog = new ShortcutDialog(item.Profile.name, icon, item.Accent, offer,
                HasShortcutIn(item.Profile, DesktopShortcutFolder()), HasShortcutIn(item.Profile, StartMenuShortcutFolder())))
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                try
                {
                    var made = CreateShortcuts(item, dialog.Desktop, dialog.StartMenu);
                    MessageBox.Show(DialogOwner, "Shortcut created:\n" + string.Join("\n", made.ToArray()) + "\n\nIt starts " + item.Profile.name +
                        " through CrossCrease, with your settings. Keep CrossCrease.exe where it is, or create the shortcut again after moving it.",
                        "Shortcut for " + item.Profile.name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(DialogOwner, ex.Message, "Could not create the shortcut", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        List<string> CreateShortcuts(LibraryItem item, bool desktop, bool startMenu)
        {
            var records = LoadShortcutRecords();
            ShortcutRecord record;
            if (!records.TryGetValue(item.Profile.id, out record) || record == null) record = new ShortcutRecord();
            var links = new List<string>();
            if (record.links != null)
                foreach (var link in record.links) if (File.Exists(link) && !links.Contains(link)) links.Add(link);

            string icon = WriteShortcutIcon(item, record);
            var made = new List<string>();
            var folders = new List<string>();
            if (desktop) folders.Add(DesktopShortcutFolder());
            if (startMenu) folders.Add(StartMenuShortcutFolder());
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(folder);
                string path = ShortcutPath(folder, item.Profile, links);
                WriteShortcut(path, item.Profile, icon);
                if (!links.Exists(delegate(string l) { return string.Equals(l, path, StringComparison.OrdinalIgnoreCase); })) links.Add(path);
                made.Add(path);
            }
            record.links = links;
            records[item.Profile.id] = record;
            SaveShortcutRecords(records);
            return made;
        }

        // "<game name>.lnk", unless a file of that name already exists that CrossCrease did not make for this game.
        string ShortcutPath(string folder, GameProfile profile, List<string> ours)
        {
            string name = SafeFileName(profile.name).Trim();
            if (name.Length == 0) name = SafeFileName(profile.id);
            string path = Path.Combine(folder, name + ".lnk");
            if (!File.Exists(path) || ours.Exists(delegate(string l) { return string.Equals(l, path, StringComparison.OrdinalIgnoreCase); }) || IsOurShortcut(path, profile))
                return path;
            return Path.Combine(folder, name + " (CrossCrease).lnk");
        }

        static string LauncherPath { get { return Path.GetFullPath(Application.ExecutablePath); } }

        static string ShortcutArguments(GameProfile profile)
        {
            return PlayArgument + " \"" + profile.id + "\"";
        }

        static bool IsOurShortcut(string path, GameProfile profile)
        {
            try
            {
                var link = (IShellLinkW)new ShellLinkObject();
                ((IPersistFile)link).Load(path, 0);
                var target = new StringBuilder(1024);
                var arguments = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                link.GetArguments(arguments, arguments.Capacity);
                return string.Equals(Path.GetFileName(target.ToString()), Path.GetFileName(LauncherPath), StringComparison.OrdinalIgnoreCase) &&
                    arguments.ToString() == ShortcutArguments(profile);
            }
            catch { return false; }
        }

        static void WriteShortcut(string path, GameProfile profile, string icon)
        {
            var link = (IShellLinkW)new ShellLinkObject();
            link.SetPath(LauncherPath);
            link.SetArguments(ShortcutArguments(profile));
            link.SetWorkingDirectory(Path.GetDirectoryName(LauncherPath));
            link.SetDescription("Play " + profile.name + " with CrossCrease");
            link.SetIconLocation(icon, 0);
            ((IPersistFile)link).Save(path, true);
            SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, path, null);
        }

        // ----- Cover icon -------------------------------------------------------------------------

        // Identifies the cover the icon was made from: which file, and when it last changed.
        string CoverSource(LibraryItem item)
        {
            string path = UserCoverPath(item.Profile) ?? DownloadedCoverPath(item.Profile) ?? ProfileCoverPath(item.Profile);
            if (path == null) return "generated:" + item.Accent.ToArgb() + ":" + item.Profile.name;
            try { var info = new FileInfo(path); return info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks; }
            catch { return path; }
        }

        // Writes the icon under a name that changes with the picture, so Explorer's icon cache cannot keep the old one.
        string WriteShortcutIcon(LibraryItem item, ShortcutRecord record)
        {
            byte[] data = BuildIcon(item);
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(data), 0, 6).Replace("-", "").ToLowerInvariant();
            Directory.CreateDirectory(ShortcutsDirectory);
            string path = Path.Combine(ShortcutsDirectory, SafeFileName(item.Profile.id) + "-" + hash + ".ico");
            if (!File.Exists(path)) File.WriteAllBytes(path, data);
            if (!string.IsNullOrWhiteSpace(record.icon) && !string.Equals(record.icon, path, StringComparison.OrdinalIgnoreCase))
                try { if (File.Exists(record.icon) && Path.GetDirectoryName(record.icon) == ShortcutsDirectory) File.Delete(record.icon); } catch { }
            record.icon = path;
            record.cover = CoverSource(item);
            return path;
        }

        // The full cover, fitted into a square with transparent sides (covers are taller than wide).
        Bitmap RenderShortcutIcon(LibraryItem item, int size)
        {
            var icon = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            Image cover = CustomCover(item.Profile);
            Bitmap generated = null;
            if (cover == null) cover = generated = CoverArt.Render(item, null, 400, 600, false, null);
            try
            {
                using (var g = Graphics.FromImage(icon))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    float scale = Math.Min(size / (float)cover.Width, size / (float)cover.Height);
                    float w = Math.Max(1f, (float)Math.Round(cover.Width * scale)), h = Math.Max(1f, (float)Math.Round(cover.Height * scale));
                    var area = new RectangleF((float)Math.Floor((size - w) / 2f), (float)Math.Floor((size - h) / 2f), w, h);
                    using (var attributes = new ImageAttributes())
                    {
                        attributes.SetWrapMode(WrapMode.TileFlipXY); // keeps the edges sharp instead of fading into transparency
                        g.DrawImage(cover, Rectangle.Round(area), 0, 0, cover.Width, cover.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
            }
            finally { if (generated != null) generated.Dispose(); }
            return icon;
        }

        // An .ico with a PNG image for every size Windows asks for (16 px in lists up to 256 px for large icons).
        byte[] BuildIcon(LibraryItem item)
        {
            var images = new List<byte[]>();
            foreach (int size in IconSizes)
                using (var bitmap = RenderShortcutIcon(item, size))
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, ImageFormat.Png);
                    images.Add(png.ToArray());
                }
            using (var ico = new MemoryStream())
            using (var writer = new BinaryWriter(ico))
            {
                writer.Write((short)0); writer.Write((short)1); writer.Write((short)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    int size = IconSizes[i];
                    writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((short)1); writer.Write((short)32);
                    writer.Write(images[i].Length); writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) writer.Write(image);
                writer.Flush();
                return ico.ToArray();
            }
        }

        // After the library reloads: give existing shortcuts a new icon when the game's cover has changed.
        void RefreshShortcutIcons()
        {
            Dictionary<string, ShortcutRecord> records;
            try { records = LoadShortcutRecords(); } catch { return; }
            if (records.Count == 0) return;
            bool changed = false;
            foreach (var item in items)
            {
                ShortcutRecord record;
                if (!records.TryGetValue(item.Profile.id, out record) || record == null || record.links == null) continue;
                if (record.cover == CoverSource(item) && File.Exists(record.icon ?? "")) continue;
                try
                {
                    string icon = WriteShortcutIcon(item, record);
                    record.links = record.links.FindAll(delegate(string link) { return File.Exists(link); });
                    foreach (var link in record.links) WriteShortcut(link, item.Profile, icon);
                    changed = true;
                }
                catch { }
            }
            if (changed) try { SaveShortcutRecords(records); } catch { }
        }

        // ----- Shell link interop ------------------------------------------------------------------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern void SHChangeNotify(int eventId, int flags, string item1, string item2);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkObject { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, int flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }

    // "Create a shortcut for <game>?" with the cover icon, where to put it, and Create / Not now.
    sealed class ShortcutDialog : Form
    {
        readonly CheckBox desktop, startMenu;
        public bool Desktop { get { return desktop.Checked; } }
        public bool StartMenu { get { return startMenu.Checked; } }

        public ShortcutDialog(string gameName, Image icon, Color accent, bool offer, bool onDesktop, bool inStartMenu)
        {
            Text = "Shortcut for " + gameName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            if (Ui.AppIcon != null) Icon = Ui.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 10f);
            int pad = Ui.S(24), width = Ui.S(440), y = pad, iconSize = Ui.S(64), textX = pad + iconSize + Ui.S(18), textWidth = width - iconSize - Ui.S(18);

            Controls.Add(new PictureBox { Image = new Bitmap(icon), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(pad, y + Ui.S(2), iconSize, iconSize) });
            var title = new Label { Text = (offer ? "Create a shortcut for " : "Shortcut for ") + gameName + (offer ? "?" : ""), Font = Ui.HeavyFont(14f),
                AutoSize = true, MaximumSize = new Size(textWidth, 0), Location = new Point(textX - Ui.S(2), y), UseMnemonic = false };
            Controls.Add(title);
            int ty = y + title.PreferredHeight + Ui.S(8);
            var note = new Label { Text = "Start " + gameName + " with one click. The shortcut shows the game's cover and starts the game through CrossCrease, with your settings.",
                ForeColor = Ui.Muted, Location = new Point(textX, ty), MaximumSize = new Size(textWidth, 0), AutoSize = true, UseMnemonic = false };
            Controls.Add(note);
            ty += note.PreferredHeight;
            bool existing = onDesktop || inStartMenu;
            if (existing)
            {
                string where = onDesktop && inStartMenu ? "on the desktop and in the Start menu" : onDesktop ? "on the desktop" : "in the Start menu";
                var have = new Label { Text = "You already have this shortcut " + where + ". Creating it again updates it.",
                    ForeColor = Ui.Dim, Font = new Font("Segoe UI", 9f), Location = new Point(textX, ty + Ui.S(6)), MaximumSize = new Size(textWidth, 0), AutoSize = true, UseMnemonic = false };
                Controls.Add(have);
                ty += Ui.S(6) + have.PreferredHeight;
            }
            y = Math.Max(y + iconSize, ty) + Ui.S(18);

            desktop = new CheckBox { Text = "On the desktop", Checked = !existing || onDesktop, AutoSize = true, Location = new Point(textX, y), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold) };
            Controls.Add(desktop);
            y += desktop.PreferredSize.Height + Ui.S(6);
            startMenu = new CheckBox { Text = "In the Start menu", Checked = !existing || inStartMenu, AutoSize = true, Location = new Point(textX, y), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold) };
            Controls.Add(startMenu);
            y += startMenu.PreferredSize.Height + Ui.S(22);

            var create = new FlatButton { Kind = ButtonKind.Primary, Accent = accent, Text = "Create shortcut" };
            var cancel = new FlatButton { Kind = ButtonKind.Ghost, Text = offer ? "Not now" : "Cancel" };
            int h = Ui.S(36), createWidth = Math.Max(Ui.S(140), create.PreferredWidth()), cancelWidth = Math.Max(Ui.S(90), cancel.PreferredWidth());
            cancel.SetBounds(pad + width - cancelWidth, y, cancelWidth, h);
            create.SetBounds(pad + width - cancelWidth - Ui.S(8) - createWidth, y, createWidth, h);
            create.Click += delegate { DialogResult = DialogResult.OK; };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            EventHandler sync = delegate { create.Enabled = desktop.Checked || startMenu.Checked; };
            desktop.CheckedChanged += sync;
            startMenu.CheckedChanged += sync;
            Controls.Add(create);
            Controls.Add(cancel);
            ClientSize = new Size(width + pad * 2, y + h + pad);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (Control control in Controls)
                {
                    var picture = control as PictureBox;
                    if (picture != null && picture.Image != null) picture.Image.Dispose();
                }
            base.Dispose(disposing);
        }
    }
}
