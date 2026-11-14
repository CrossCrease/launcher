using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CrossCrease
{
    // Downloads, updates, per-game settings and extra launch options for the main window.
    public sealed partial class MainForm
    {
        sealed class InstallJob
        {
            public string ProfileId;
            public string Phase = "Preparing…";
            public string Detail = "";
            public float Fraction;
            // Package installs can be stopped; downloads finish on their own.
            public bool CanCancel;
            public volatile bool CancelRequested;
        }

        readonly PackageStore packages;
        readonly Dictionary<string, PackageFeed> feeds = new Dictionary<string, PackageFeed>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> feedErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> feedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        PackageFeed launcherFeed;
        bool checkingFeeds;
        InstallJob installing;
        DateTime lastProgressPaint = DateTime.MinValue;

        static readonly Regex PackageToken = new Regex(@"\{package:([A-Za-z0-9._-]+)\}");
        static readonly Regex SettingToken = new Regex(@"\{setting:([A-Za-z0-9._-]+)\}");

        bool IsInstalling(LibraryItem item) { return installing != null && item != null && installing.ProfileId == item.Profile.id; }

        // ----- Feeds and update checks ---------------------------------------------------------

        // Testers and developers can point a game (or "launcher") at another feed, such as a local
        // file:/// feed produced by a game project's packaging script, in %LOCALAPPDATA%\CrossCrease\feeds.json.
        void LoadFeedOverrides()
        {
            try
            {
                string file = Path.Combine(dataDirectory, "feeds.json");
                if (!File.Exists(file)) return;
                var loaded = serializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (loaded != null) feedOverrides = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        string FeedUrl(GameProfile profile)
        {
            string url;
            if (feedOverrides.TryGetValue(profile.id, out url) && !string.IsNullOrWhiteSpace(url)) return url;
            return string.IsNullOrWhiteSpace(profile.package_feed) ? null : profile.package_feed;
        }

        // Runs at startup and on Refresh. Network problems are reported on the affected game only.
        void BeginUpdateCheck()
        {
            BeginCoverSync(false);
            if (checkingFeeds) return;
            var targets = new List<KeyValuePair<string, string>>();
            foreach (var item in items)
            {
                string url = FeedUrl(item.Profile);
                if (url != null) targets.Add(new KeyValuePair<string, string>(item.Profile.id, url));
            }
            string launcherUrl;
            if (!feedOverrides.TryGetValue("launcher", out launcherUrl)) launcherUrl = BuildInfo.UpdateFeed;
            checkingFeeds = true;
            if (library.SelectedItem != null && library.SelectedItem.State == GameState.NeedsDownload) UpdateHero();
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new Dictionary<string, PackageFeed>(StringComparer.OrdinalIgnoreCase);
                var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in targets)
                {
                    try { found[target.Key] = packages.FetchFeed(target.Value); }
                    catch (Exception ex) { errors[target.Key] = FriendlyError(ex); }
                }
                PackageFeed newestLauncher = null;
                try { if (!string.IsNullOrWhiteSpace(launcherUrl)) newestLauncher = packages.FetchFeed(launcherUrl); } catch { }
                SafeInvoke(delegate
                {
                    checkingFeeds = false;
                    foreach (var target in targets)
                    {
                        feeds.Remove(target.Key);
                        feedErrors.Remove(target.Key);
                    }
                    foreach (var pair in found) feeds[pair.Key] = pair.Value;
                    foreach (var pair in errors) feedErrors[pair.Key] = pair.Value;
                    launcherFeed = newestLauncher;
                    UpdateLauncherButton();
                    var selected = library.SelectedItem;
                    LoadProfiles(selected == null ? null : selected.Profile.id);
                });
            });
        }

        void SafeInvoke(MethodInvoker action)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        static string FriendlyError(Exception ex)
        {
            var web = ex as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return "No download has been published for this game yet.";
                return "CrossCrease could not reach the download server. Check your internet connection.";
            }
            if (ex is PackageException) return ex.Message;
            if (ex is IOException || ex is UnauthorizedAccessException) return "The files could not be written: " + ex.Message;
            return ex.Message;
        }

        void UpdateLauncherButton()
        {
            bool newer = launcherFeed != null && !string.IsNullOrWhiteSpace(launcherFeed.release) &&
                PackageStore.CompareVersions(launcherFeed.release, BuildInfo.Version) > 0;
            topBar.UpdateButton.Visible = newer;
            topBar.UpdateButton.Enabled = !updatingLauncher;
            if (newer && !updatingLauncher) topBar.UpdateButton.Text = "Update launcher to " + launcherFeed.release;
            topBar.PerformLayout();
            topBar.Invalidate(true);
        }

        void OpenLauncherRelease()
        {
            string url = launcherFeed != null ? launcherFeed.notes_url : null;
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = BuildInfo.ReleasesPage;
            try { Process.Start(url); } catch { }
        }

        bool LauncherTooOld(PackageFeed feed)
        {
            return feed != null && !string.IsNullOrWhiteSpace(feed.minimum_launcher) &&
                PackageStore.CompareVersions(BuildInfo.Version, feed.minimum_launcher) < 0;
        }

        // ----- Package state -------------------------------------------------------------------

        IEnumerable<string> AllPackageIds(GameProfile profile)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (profile.packages != null) foreach (var id in profile.packages) if (seen.Add(id)) yield return id;
            if (profile.launch_options != null)
                foreach (var option in profile.launch_options)
                    if (option.packages != null) foreach (var id in option.packages) if (seen.Add(id)) yield return id;
        }

        List<string> MissingPackages(GameProfile profile, string[] ids)
        {
            var missing = new List<string>();
            if (ids != null)
                foreach (var id in ids)
                    if (packages.InstalledPath(profile.id, id) == null || !MeetsMinimum(profile, id, packages.InstalledVersion(profile.id, id))) missing.Add(id);
            return missing;
        }

        static bool FeedMeetsMinimums(GameProfile profile, PackageFeed feed, IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                var offered = feed.Find(id);
                if (offered != null && !MeetsMinimum(profile, id, offered.version)) return false;
            }
            return true;
        }

        internal static bool MeetsMinimum(GameProfile profile, string packageId, string version)
        {
            string minimum;
            if (profile.minimum_versions == null || version == null) return true;
            foreach (var pair in profile.minimum_versions)
                if (string.Equals(pair.Key, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    minimum = pair.Value;
                    return string.IsNullOrWhiteSpace(minimum) || PackageStore.CompareVersions(version, minimum) >= 0;
                }
            return true;
        }

        void ApplyPackageState(LibraryItem item)
        {
            item.MissingPackages = MissingPackages(item.Profile, item.Profile.packages);
            item.Updates.Clear();
            PackageFeed feed;
            if (!feeds.TryGetValue(item.Profile.id, out feed)) return;
            foreach (var id in AllPackageIds(item.Profile))
            {
                string installed = packages.InstalledVersion(item.Profile.id, id);
                var offered = feed.Find(id);
                if (installed != null && offered != null && PackageStore.CompareVersions(offered.version, installed) > 0)
                    item.Updates.Add(offered);
            }
        }

        string ExpandTokens(GameProfile profile, string value)
        {
            if (value == null) return null;
            string result = value.Replace("{launcher}", baseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            try { if (result.Contains("{data}")) result = result.Replace("{data}", packages.GameDirectory(profile.id)); }
            catch (PackageException) { return null; }
            bool unresolved = false;
            result = PackageToken.Replace(result, delegate(Match match)
            {
                string path = packages.InstalledPath(profile.id, match.Groups[1].Value);
                if (path == null) { unresolved = true; return ""; }
                return path;
            });
            result = SettingToken.Replace(result, delegate(Match match)
            {
                // Values are validated against the setting's pattern; quotes are never passed through.
                return (SettingValue(profile, match.Groups[1].Value) ?? "").Replace("\"", "");
            });
            return unresolved ? null : result;
        }

        string GameFolder(LibraryItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Root) && Directory.Exists(item.Root)) return item.Root;
            return ResolveWorkingDirectory(item.Profile);
        }

        long DownloadSize(PackageFeed feed, IEnumerable<string> ids)
        {
            long total = 0;
            foreach (var id in ids)
            {
                var package = feed.Find(id);
                if (package != null) total += package.size;
            }
            return total;
        }

        static string WithSize(string text, long size)
        {
            string formatted = PackageStore.FormatSize(size);
            return formatted.Length == 0 ? text : text + " (" + formatted + ")";
        }

        // ----- Selection bar -------------------------------------------------------------------

        void DownloadButtonClicked(LibraryItem item)
        {
            PackageFeed feed;
            if (!feeds.TryGetValue(item.Profile.id, out feed)) { BeginUpdateCheck(); return; }
            if (LauncherTooOld(feed)) { OpenLauncherRelease(); return; }
            InstallPackages(item, item.MissingPackages);
        }

        FlatButton UpdateButton(LibraryItem item)
        {
            if (item.Updates.Count == 0) return null;
            long size = 0;
            var ids = new List<string>();
            foreach (var package in item.Updates) { size += package.size; ids.Add(package.id); }
            var button = new FlatButton { Kind = ButtonKind.Secondary, Text = WithSize("Update", size) };
            button.Click += delegate { InstallPackages(item, ids); };
            return button;
        }

        string UpdateSummary(LibraryItem item)
        {
            var parts = new List<string>();
            foreach (var package in item.Updates)
                parts.Add((package.name ?? package.id) + " " + packages.InstalledVersion(item.Profile.id, package.id) + " → " + package.version);
            return string.Join(", ", parts.ToArray()) + " · saves and settings are kept";
        }

        IEnumerable<FlatButton> LaunchOptionButtons(LibraryItem item)
        {
            if (item.Profile.launch_options == null) yield break;
            foreach (var option in item.Profile.launch_options)
            {
                if (string.IsNullOrWhiteSpace(option.label)) continue;
                var captured = option;
                var button = new FlatButton { Kind = ButtonKind.Secondary, Text = option.label };
                button.Click += delegate { RunLaunchOption(item, captured); };
                yield return button;
            }
        }

        void RunLaunchOption(LibraryItem item, LaunchOption option)
        {
            var missing = MissingPackages(item.Profile, option.packages);
            if (missing.Count == 0) { LaunchGame(item, option); return; }
            PackageFeed feed;
            if (!feeds.TryGetValue(item.Profile.id, out feed))
            {
                MessageBox.Show(DialogOwner, "The files for \"" + option.label + "\" are not installed and the download list is not available right now.\n\n" +
                    (feedErrors.ContainsKey(item.Profile.id) ? feedErrors[item.Profile.id] : "Try Refresh in a moment."),
                    option.label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var names = new List<string>();
            foreach (var id in missing) { var package = feed.Find(id); names.Add(package == null ? id : package.name ?? id); }
            string size = PackageStore.FormatSize(DownloadSize(feed, missing));
            if (MessageBox.Show(DialogOwner, "\"" + option.label + "\" needs " + string.Join(", ", names.ToArray()) +
                    (size.Length > 0 ? " (" + size + ")" : "") + ".\n\nDownload and install now?",
                    option.label, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            InstallPackages(item, missing);
        }

        bool PrepareLaunchOption(LibraryItem item, LaunchOption option)
        {
            if (MissingPackages(item.Profile, option.packages).Count > 0) { RunLaunchOption(item, option); return false; }
            return EnsureSettings(item, option.settings, option.label);
        }

        // Asks once for any of the listed settings that are not saved yet. Returns false on cancel.
        bool EnsureSettings(LibraryItem item, string[] ids, string purpose)
        {
            if (ids == null || ids.Length == 0) return true;
            var saved = LoadSettings(item.Profile);
            bool complete = true;
            foreach (var id in ids)
            {
                var setting = FindSetting(item.Profile, id);
                string value;
                if (setting == null) continue;
                if (!saved.TryGetValue(id, out value) || !SettingValid(setting, value)) complete = false;
            }
            return complete || ShowSettingsDialog(item, ids, purpose);
        }

        // ----- Installing ----------------------------------------------------------------------

        void InstallPackages(LibraryItem item, IList<string> ids)
        {
            if (installing != null || item == null || ids == null || ids.Count == 0) return;
            PackageFeed feed;
            if (!feeds.TryGetValue(item.Profile.id, out feed)) { BeginUpdateCheck(); return; }
            if (LauncherTooOld(feed)) { OpenLauncherRelease(); return; }
            var queue = new List<FeedPackage>();
            foreach (var id in ids)
            {
                var package = feed.Find(id);
                if (package == null)
                {
                    MessageBox.Show(DialogOwner, "The current " + item.Profile.name + " release does not include \"" + id + "\".",
                        "Download not available", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                queue.Add(package);
            }
            if (GameRunningFrom(item.Profile))
            {
                MessageBox.Show(DialogOwner, "Close " + item.Profile.name + " before installing or updating its files.",
                    item.Profile.name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var job = new InstallJob { ProfileId = item.Profile.id };
            installing = job;
            string profileId = item.Profile.id, gameName = item.Profile.name;
            UpdateHero();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string failure = null;
                try
                {
                    for (int i = 0; i < queue.Count; i++)
                    {
                        var package = queue[i];
                        int index = i;
                        packages.Install(profileId, feed, package, delegate(long done, long total, string phase)
                        {
                            float part = total > 0 ? Math.Min(1f, done / (float)total) : 0f;
                            job.Fraction = (index + (phase == "Downloading" ? part * 0.92f : phase == "Verifying" ? 0.94f : 0.97f)) / queue.Count;
                            job.Phase = phase == "Downloading" ? "Downloading " + (int)Math.Round(job.Fraction * 100) + "%" : phase + "…";
                            job.Detail = (package.name ?? package.id) + " " + package.version +
                                (phase == "Downloading" && total > 0 ? " · " + PackageStore.FormatSize(done) + " of " + PackageStore.FormatSize(total) : "");
                            SafeInvoke(ShowProgress);
                        });
                    }
                }
                catch (Exception ex) { failure = FriendlyError(ex); }
                SafeInvoke(delegate
                {
                    installing = null;
                    LoadProfiles(profileId);
                    if (failure != null)
                        MessageBox.Show(DialogOwner, failure, "Could not install " + gameName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            });
        }

        void ShowProgress()
        {
            if (installing == null) return;
            if ((DateTime.UtcNow - lastProgressPaint).TotalMilliseconds < 60) return;
            lastProgressPaint = DateTime.UtcNow;
            var item = library.SelectedItem;
            if (IsInstalling(item)) UpdateHero();
        }

        bool GameRunningFrom(GameProfile profile)
        {
            string folder;
            try { folder = Path.Combine(packages.GameDirectory(profile.id), "packages") + Path.DirectorySeparatorChar; }
            catch { return false; }
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    string path = process.MainModule.FileName;
                    if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }

        // ----- Menu ----------------------------------------------------------------------------

        void AddPackageMenuItems(LibraryItem item)
        {
            var profile = item.Profile;
            if (FeedUrl(profile) != null)
                AddMenuItem("Check for updates", delegate { BeginUpdateCheck(); }).Enabled = !checkingFeeds && installing == null;
            if (packages.HasPackages(profile.id))
            {
                AddMenuItem("Open data folder", delegate { OpenFolder(packages.GameDirectory(profile.id)); });
                AddMenuItem("Remove downloaded files…", delegate { RemoveDownloads(item); }).Enabled = installing == null;
            }
        }

        // Switches a package back to the version kept on disk. The newer version stays installed, so the
        // update offered afterwards is applied without downloading again.
        void RollBackPackage(LibraryItem item, string packageId)
        {
            if (item == null || installing != null) return;
            string current = packages.InstalledVersion(item.Profile.id, packageId), previous = packages.PreviousVersion(item.Profile.id, packageId);
            if (current == null || previous == null) return;
            if (GameRunningFrom(item.Profile))
            {
                MessageBox.Show(DialogOwner, "Close " + item.Profile.name + " first.", item.Profile.name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(DialogOwner, "Go back from " + current + " to " + previous + "?\n\n" + current + " stays on this PC, and the launcher offers it " +
                    "again as an update (installed without a new download). Your saves and settings are kept.",
                    "Roll back " + item.Profile.name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { packages.RollBack(item.Profile.id, packageId); }
            catch (Exception ex) { MessageBox.Show(DialogOwner, FriendlyError(ex), "Could not roll back", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            LoadProfiles(item.Profile.id);
        }

        void OpenFolder(string folder)
        {
            if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        void RemoveDownloads(LibraryItem item)
        {
            if (GameRunningFrom(item.Profile))
            {
                MessageBox.Show(DialogOwner, "Close " + item.Profile.name + " first.", item.Profile.name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(DialogOwner, "Remove the downloaded " + item.Profile.name + " files?\n\nYour saves and settings are kept, " +
                    "and you can download the files again at any time.", "Remove downloaded files",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { packages.RemovePackages(item.Profile.id); }
            catch (Exception ex) { MessageBox.Show(DialogOwner, ex.Message, "Could not remove files", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            LoadProfiles(item.Profile.id);
        }

        // ----- Per-game settings ---------------------------------------------------------------

        string SettingsFile(GameProfile profile) { return Path.Combine(packages.GameDirectory(profile.id), "settings.json"); }

        Dictionary<string, string> LoadSettings(GameProfile profile)
        {
            try
            {
                string file = SettingsFile(profile);
                if (File.Exists(file))
                {
                    var loaded = serializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                    if (loaded != null) return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        void SaveSettings(GameProfile profile, Dictionary<string, string> values)
        {
            string file = SettingsFile(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, serializer.Serialize(values));
        }

        static ProfileSetting FindSetting(GameProfile profile, string id)
        {
            if (profile.settings == null) return null;
            foreach (var setting in profile.settings)
                if (string.Equals(setting.id, id, StringComparison.OrdinalIgnoreCase)) return setting;
            return null;
        }

        static bool SettingValid(ProfileSetting setting, string value)
        {
            if (string.IsNullOrEmpty(value) || value.Contains("\"")) return false;
            if (string.IsNullOrWhiteSpace(setting.pattern)) return true;
            try { return Regex.IsMatch(value, setting.pattern); } catch { return false; }
        }

        string SettingValue(GameProfile profile, string id)
        {
            string value;
            var setting = FindSetting(profile, id);
            if (LoadSettings(profile).TryGetValue(id, out value) && (setting == null || SettingValid(setting, value))) return value;
            return setting != null ? setting.@default : null;
        }

        // Asks for the listed settings (all of the profile's settings when ids is null). Returns false on cancel.
        bool ShowSettingsDialog(LibraryItem item, string[] ids, string purpose)
        {
            var profile = item.Profile;
            var shown = new List<ProfileSetting>();
            if (profile.settings != null)
                foreach (var setting in profile.settings)
                    if (ids == null || Array.Exists(ids, delegate(string id) { return string.Equals(id, setting.id, StringComparison.OrdinalIgnoreCase); }))
                        shown.Add(setting);
            if (shown.Count == 0) return true;
            var values = LoadSettings(profile);
            using (var dialog = new SettingsDialog(profile.name + (purpose != null ? " · " + purpose : " settings"), shown, values, item.Accent))
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return false;
                foreach (var pair in dialog.Values) values[pair.Key] = pair.Value;
                try { SaveSettings(profile, values); }
                catch (Exception ex)
                {
                    MessageBox.Show(DialogOwner, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                return true;
            }
        }
    }

    sealed class SettingsDialog : Form
    {
        readonly List<ProfileSetting> settings;
        readonly List<TextBox> boxes = new List<TextBox>();
        readonly Label error;
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public SettingsDialog(string title, List<ProfileSetting> settings, Dictionary<string, string> current, Color accent)
        {
            this.settings = settings;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 10f);
            int pad = Ui.S(24), width = Ui.S(420), y = pad;
            foreach (var setting in settings)
            {
                Controls.Add(new Label { Text = setting.label ?? setting.id, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), AutoSize = true, Location = new Point(pad, y) });
                y += Ui.S(26);
                if (!string.IsNullOrWhiteSpace(setting.description))
                {
                    var description = new Label { Text = setting.description, ForeColor = Ui.Muted, Location = new Point(pad, y), MaximumSize = new Size(width, 0), AutoSize = true };
                    Controls.Add(description);
                    y += description.PreferredHeight + Ui.S(6);
                }
                string value;
                var box = new TextBox {
                    Text = current.TryGetValue(setting.id, out value) ? value : (setting.@default ?? ""),
                    Location = new Point(pad, y), Width = width, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle
                };
                boxes.Add(box);
                Controls.Add(box);
                y += box.Height + Ui.S(18);
            }
            error = new Label { ForeColor = Ui.Amber, Location = new Point(pad, y), MaximumSize = new Size(width, 0), AutoSize = true };
            Controls.Add(error);
            y += Ui.S(26);
            var save = new FlatButton { Kind = ButtonKind.Primary, Accent = accent, Text = "Save" };
            var cancel = new FlatButton { Kind = ButtonKind.Secondary, Text = "Cancel" };
            int h = Ui.S(38), saveWidth = Math.Max(Ui.S(110), save.PreferredWidth()), cancelWidth = cancel.PreferredWidth();
            save.SetBounds(pad + width - saveWidth, y, saveWidth, h);
            cancel.SetBounds(save.Left - Ui.S(10) - cancelWidth, y, cancelWidth, h);
            save.Click += delegate { Accept(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(save);
            Controls.Add(cancel);
            ClientSize = new Size(width + pad * 2, y + h + pad);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Enter) { Accept(); return true; }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessDialogKey(keyData);
        }

        void Accept()
        {
            for (int i = 0; i < settings.Count; i++)
            {
                string value = boxes[i].Text.Trim();
                bool valid = value.Length > 0 && !value.Contains("\"");
                if (valid && !string.IsNullOrWhiteSpace(settings[i].pattern))
                    try { valid = Regex.IsMatch(value, settings[i].pattern); } catch { valid = false; }
                if (!valid)
                {
                    error.Text = (settings[i].label ?? settings[i].id) + " is not valid." +
                        (string.IsNullOrWhiteSpace(settings[i].description) ? "" : " " + settings[i].description);
                    boxes[i].Focus();
                    return;
                }
                Values[settings[i].id] = value;
            }
            DialogResult = DialogResult.OK;
        }
    }
}
