using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CrossCrease
{
    // One place for everything a player may want to change for a game: where its files are, the
    // CrossCrease downloads, player settings such as the online ID, and the cover image.
    public sealed partial class MainForm
    {
        GameSettingsWindow settingsWindow;

        // Dialogs opened from the settings window must belong to it, not to the (disabled) main window.
        IWin32Window DialogOwner
        {
            get { return settingsWindow != null && !settingsWindow.IsDisposed && settingsWindow.Visible ? (IWin32Window)settingsWindow : this; }
        }

        LibraryItem ItemById(string id)
        {
            return items.Find(delegate(LibraryItem item) { return item.Profile.id == id; });
        }

        void ShowGameSettings(LibraryItem item)
        {
            if (item == null || item.State == GameState.Planned || settingsWindow != null) return;
            using (settingsWindow = new GameSettingsWindow(this, item.Profile.id))
                settingsWindow.ShowDialog(this);
            settingsWindow = null;
            UpdateHero();
        }

        sealed class GameSettingsWindow : Form
        {
            readonly MainForm owner;
            readonly string profileId;
            readonly Panel content;
            readonly Timer watcher = new Timer { Interval = 250 };
            readonly Dictionary<string, TextBox> settingBoxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> typed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string signature, settingsMessage;
            bool settingsMessageIsError;
            int y;

            int Pad { get { return Ui.S(28); } }
            int ContentWidth { get { return Ui.S(600); } }

            public GameSettingsWindow(MainForm owner, string profileId)
            {
                this.owner = owner;
                this.profileId = profileId;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(22, 25, 31);
                ForeColor = Ui.Text;
                Font = new Font("Segoe UI", 10f);
                KeyPreview = true;
                content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
                Controls.Add(content);
                watcher.Tick += delegate { if (Signature() != signature) Rebuild(); };
                Rebuild();
                watcher.Start();
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) watcher.Dispose();
                base.Dispose(disposing);
            }

            LibraryItem Item { get { return owner.ItemById(profileId); } }

            // Rebuilds only when something the window shows has changed (install progress, location, ...).
            string Signature()
            {
                var item = Item;
                if (item == null) return "gone";
                var parts = new List<string> { item.State.ToString(), item.Root ?? "", owner.HasSavedLocation(item.Profile).ToString(),
                    owner.checkingFeeds ? "checking" : "idle", owner.UserCoverPath(item.Profile) ?? "", owner.DownloadedCoverPath(item.Profile) ?? "" };
                if (owner.IsInstalling(item)) { parts.Add(owner.installing.Phase); parts.Add(owner.installing.Detail); }
                foreach (var id in owner.AllPackageIds(item.Profile)) parts.Add(id + "=" + owner.packages.InstalledVersion(item.Profile.id, id));
                PackageFeed feed;
                if (owner.feeds.TryGetValue(item.Profile.id, out feed)) parts.Add(feed.release);
                string error;
                if (owner.feedErrors.TryGetValue(item.Profile.id, out error)) parts.Add(error);
                return string.Join("|", parts.ToArray());
            }

            void Rebuild()
            {
                var item = Item;
                if (item == null) { Close(); return; }
                foreach (var pair in settingBoxes) typed[pair.Key] = pair.Value.Text;
                settingBoxes.Clear();
                signature = Signature();
                int scroll = -content.AutoScrollPosition.Y;
                content.SuspendLayout();
                var old = new Control[content.Controls.Count];
                content.Controls.CopyTo(old, 0);
                content.Controls.Clear();
                foreach (var control in old)
                {
                    var picture = control as PictureBox;
                    if (picture != null && picture.Image != null) picture.Image.Dispose();
                    control.Dispose();
                }
                Text = item.Profile.name + " · Game settings";
                y = Pad;

                AddHeader(item);
                AddGameFiles(item);
                if (owner.FeedUrl(item.Profile) != null) AddDownloads(item);
                if (item.Profile.settings != null && item.Profile.settings.Length > 0) AddPlayerSettings(item);
                AddCover(item);

                var close = new FlatButton { Kind = ButtonKind.Primary, Accent = item.Accent, Text = "Done" };
                PlaceButtons(new[] { close }, true);
                close.Click += delegate { Close(); };
                y += Pad;

                var screen = Screen.FromControl(owner).WorkingArea;
                ClientSize = new Size(ContentWidth + Pad * 2 + SystemInformation.VerticalScrollBarWidth, Math.Min(y, screen.Height * 85 / 100));
                content.ResumeLayout();
                content.AutoScrollPosition = new Point(0, scroll);
            }

            // ----- Sections ------------------------------------------------------------------

            void AddHeader(LibraryItem item)
            {
                int h = Ui.S(84), w = h * 2 / 3;
                // A private copy: the launcher's poster cache is cleared whenever the library reloads.
                var poster = new Bitmap(owner.Poster(item, w, h, false));
                var picture = new PictureBox { Image = poster, SizeMode = PictureBoxSizeMode.StretchImage, Bounds = new Rectangle(Pad, y, w, h) };
                content.Controls.Add(picture);
                int x = Pad + w + Ui.S(18);
                content.Controls.Add(new Label { Text = item.Profile.name, Font = Ui.HeavyFont(17f), AutoSize = true, Location = new Point(x - Ui.S(3), y + Ui.S(8)), BackColor = Color.Transparent });
                var state = new Label {
                    Text = (string.IsNullOrWhiteSpace(item.Profile.system) ? "" : item.Profile.system.ToUpperInvariant() + "   ") + "● " + Ui.StateTitle(item),
                    ForeColor = Ui.Muted, AutoSize = true, Location = new Point(x, y + Ui.S(46)), Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)
                };
                content.Controls.Add(state);
                y += h + Ui.S(10);
            }

            void AddGameFiles(LibraryItem item)
            {
                Heading("Game files", "Where your copy of " + item.Profile.name + " is. CrossCrease never downloads game files.");
                string relative = MainForm.GameFileRelative(item.Profile);
                if (item.GameFound)
                {
                    Value(item.Root, Ui.Text);
                    string version = MainForm.GameVersionSummary(item.Root);
                    if (version != null) Value(version, version.Contains("no update") ? Ui.Amber : Ui.Muted);
                    Value(owner.HasSavedLocation(item.Profile) ? "Chosen by you." : "Found automatically.", Ui.Dim);
                }
                else
                {
                    Value("Not found yet." + (relative != null ? " Select the game's " + Path.GetFileName(relative) + " or the folder that contains it." : ""), Ui.Amber);
                }
                var buttons = new List<FlatButton>();
                if (relative != null)
                    buttons.Add(Button("Select " + Path.GetFileName(relative) + "…", item.GameFound ? ButtonKind.Secondary : ButtonKind.Primary, item.Accent,
                        delegate { owner.ChooseGameFile(Item); }));
                buttons.Add(Button("Select folder…", ButtonKind.Secondary, item.Accent, delegate { owner.ChooseGameFolder(Item); }));
                if (owner.HasSavedLocation(item.Profile))
                    buttons.Add(Button("Use automatic detection", ButtonKind.Secondary, item.Accent, delegate { owner.ResetGameLocation(Item); }));
                if (item.GameFound)
                    buttons.Add(Button("Open folder", ButtonKind.Ghost, item.Accent, delegate { owner.OpenGameFolder(Item); }));
                PlaceButtons(buttons.ToArray(), false);
                Note("Updates go next to the game in a folder with the same name plus \"-UPDATE\".");
            }

            void AddDownloads(LibraryItem item)
            {
                var profile = item.Profile;
                Heading("CrossCrease downloads", "The runtime and other files CrossCrease installs for this game. Updating keeps your saves.");
                PackageFeed feed;
                owner.feeds.TryGetValue(profile.id, out feed);
                var missing = new List<string>();
                var updates = new List<string>();
                foreach (var id in owner.AllPackageIds(profile))
                {
                    var offered = feed != null ? feed.Find(id) : null;
                    string name = offered != null && !string.IsNullOrWhiteSpace(offered.name) ? offered.name : id;
                    string installed = owner.packages.InstalledVersion(profile.id, id);
                    string line;
                    Color color = Ui.Text;
                    if (installed == null)
                    {
                        missing.Add(id);
                        line = name + ": not installed";
                        color = Ui.Muted;
                    }
                    else if (offered != null && PackageStore.CompareVersions(offered.version, installed) > 0)
                    {
                        updates.Add(id);
                        line = name + ": " + installed + " installed, " + offered.version + " available";
                        color = Ui.Blue;
                    }
                    else line = name + ": " + installed + " installed" + (offered != null ? " (latest)" : "");
                    Value(line, color);
                }

                string error;
                if (owner.IsInstalling(item)) Value(owner.installing.Phase + "  " + owner.installing.Detail, Ui.Blue);
                else if (owner.checkingFeeds) Value("Checking for updates…", Ui.Dim);
                else if (owner.feedErrors.TryGetValue(profile.id, out error)) Value(error, Ui.Amber);
                else if (feed != null) Value("Latest release: " + feed.release + (string.IsNullOrWhiteSpace(feed.published) ? "" : " (" + feed.published + ")"), Ui.Dim);

                bool busy = owner.installing != null;
                var buttons = new List<FlatButton>();
                if (feed != null && missing.Count > 0)
                {
                    var b = Button(MainForm.WithSize("Download", owner.DownloadSize(feed, missing)), ButtonKind.Primary, item.Accent,
                        delegate { owner.InstallPackages(Item, missing); });
                    b.Enabled = !busy;
                    buttons.Add(b);
                }
                if (feed != null && updates.Count > 0)
                {
                    var b = Button(MainForm.WithSize("Update", owner.DownloadSize(feed, updates)), ButtonKind.Primary, item.Accent,
                        delegate { owner.InstallPackages(Item, updates); });
                    b.Enabled = !busy;
                    buttons.Add(b);
                }
                var check = Button("Check for updates", ButtonKind.Secondary, item.Accent, delegate { owner.BeginUpdateCheck(); });
                check.Enabled = !busy && !owner.checkingFeeds;
                buttons.Add(check);
                if (owner.packages.HasPackages(profile.id))
                {
                    var remove = Button("Remove downloads…", ButtonKind.Secondary, item.Accent, delegate { owner.RemoveDownloads(Item); });
                    remove.Enabled = !busy;
                    buttons.Add(remove);
                }
                string dataFolder = owner.packages.GameDirectory(profile.id);
                if (Directory.Exists(dataFolder))
                    buttons.Add(Button("Open saves & data folder", ButtonKind.Ghost, item.Accent, delegate { owner.OpenFolder(dataFolder); }));
                PlaceButtons(buttons.ToArray(), false);
            }

            void AddPlayerSettings(LibraryItem item)
            {
                var profile = item.Profile;
                Heading("Player settings", "Used by options such as Play online.");
                var saved = owner.LoadSettings(profile);
                foreach (var setting in profile.settings)
                {
                    content.Controls.Add(new Label { Text = setting.label ?? setting.id, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), AutoSize = true, Location = new Point(Pad, y) });
                    y += Ui.S(24);
                    if (!string.IsNullOrWhiteSpace(setting.description)) Value(setting.description, Ui.Muted);
                    string value;
                    if (!typed.TryGetValue(setting.id, out value) && !saved.TryGetValue(setting.id, out value)) value = setting.@default ?? "";
                    var box = new TextBox {
                        Text = value, Location = new Point(Pad, y), Width = Ui.S(320),
                        BackColor = Color.FromArgb(38, 42, 50), ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle
                    };
                    settingBoxes[setting.id] = box;
                    content.Controls.Add(box);
                    y += box.Height + Ui.S(14);
                }
                if (settingsMessage != null) Value(settingsMessage, settingsMessageIsError ? Ui.Amber : Ui.Green);
                PlaceButtons(new[] { Button("Save player settings", ButtonKind.Secondary, item.Accent, delegate { SavePlayerSettings(); }) }, false);
            }

            void SavePlayerSettings()
            {
                var item = Item;
                if (item == null) return;
                var values = owner.LoadSettings(item.Profile);
                foreach (var setting in item.Profile.settings)
                {
                    TextBox box;
                    if (!settingBoxes.TryGetValue(setting.id, out box)) continue;
                    string value = box.Text.Trim();
                    if (!MainForm.SettingValid(setting, value))
                    {
                        settingsMessage = (setting.label ?? setting.id) + " is not valid." + (string.IsNullOrWhiteSpace(setting.description) ? "" : " " + setting.description);
                        settingsMessageIsError = true;
                        Rebuild();
                        return;
                    }
                    values[setting.id] = value;
                }
                try
                {
                    owner.SaveSettings(item.Profile, values);
                    typed.Clear();
                    foreach (var pair in settingBoxes) typed[pair.Key] = pair.Value.Text.Trim();
                    settingsMessage = "Saved.";
                    settingsMessageIsError = false;
                }
                catch (Exception ex) { settingsMessage = ex.Message; settingsMessageIsError = true; }
                Rebuild();
            }

            void AddCover(LibraryItem item)
            {
                bool custom = owner.UserCoverPath(item.Profile) != null;
                string downloaded = owner.DownloadedCoverDescription(item.Profile);
                Heading("Cover", "The cover shown in the library. Covers are downloaded to and kept on this PC only.");
                Value(custom ? "Using your own image." : downloaded != null ? "Using " + downloaded + "." : "Using the CrossCrease cover.", Ui.Muted);
                var buttons = new List<FlatButton> { Button("Choose image…", ButtonKind.Secondary, item.Accent, delegate { owner.ChooseCover(Item); }) };
                if (custom) buttons.Add(Button("Reset cover", ButtonKind.Secondary, item.Accent, delegate { owner.ResetCover(Item); }));
                PlaceButtons(buttons.ToArray(), false);
            }

            // ----- Layout helpers --------------------------------------------------------------

            void Heading(string title, string description)
            {
                y += Ui.S(14);
                content.Controls.Add(new Panel { BackColor = Color.FromArgb(40, 45, 54), Bounds = new Rectangle(Pad, y, ContentWidth, Math.Max(1, Ui.S(1))) });
                y += Ui.S(16);
                content.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold), AutoSize = true, Location = new Point(Pad - Ui.S(2), y) });
                y += Ui.S(30);
                if (!string.IsNullOrWhiteSpace(description)) Value(description, Ui.Dim);
                y += Ui.S(4);
            }

            void Value(string text, Color color)
            {
                var label = new Label { Text = text, ForeColor = color, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Location = new Point(Pad, y), UseMnemonic = false };
                content.Controls.Add(label);
                y += label.PreferredHeight + Ui.S(4);
            }

            void Note(string text)
            {
                var label = new Label { Text = text, ForeColor = Ui.Dim, Font = new Font("Segoe UI", 9f), AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Location = new Point(Pad, y) };
                content.Controls.Add(label);
                y += label.PreferredHeight + Ui.S(4);
            }

            FlatButton Button(string text, ButtonKind kind, Color accent, EventHandler click)
            {
                var button = new FlatButton { Kind = kind, Accent = accent, Text = text, Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold) };
                button.Click += click;
                return button;
            }

            void PlaceButtons(FlatButton[] buttons, bool alignRight)
            {
                if (buttons.Length == 0) return;
                y += Ui.S(6);
                int h = Ui.S(36), gap = Ui.S(8), x = Pad;
                if (alignRight)
                {
                    int total = 0;
                    foreach (var b in buttons) total += Math.Max(Ui.S(110), b.PreferredWidth()) + gap;
                    x = Pad + ContentWidth - total + gap;
                }
                foreach (var button in buttons)
                {
                    int w = Math.Max(alignRight ? Ui.S(110) : 0, button.PreferredWidth());
                    if (!alignRight && x > Pad && x + w > Pad + ContentWidth) { x = Pad; y += h + gap; }
                    button.SetBounds(x, y, w, h);
                    content.Controls.Add(button);
                    x += w + gap;
                }
                y += h + Ui.S(10);
            }
        }
    }
}
