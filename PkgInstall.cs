using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CrossCrease
{
    // How a profile's game can be installed from the player's own package files.
    public sealed class PackageInstallOptions
    {
        // "ps4_pkg": PS4 .pkg files (see Ps4Package).
        public string format { get; set; }
        // Accepted title IDs, for example CUSA04326. Empty accepts any game.
        public string[] title_ids { get; set; }
        // Default parent folder. The game goes into <folder>\<title id> and its update into <folder>\<title id>-UPDATE.
        public string folder { get; set; }
    }

    // Installing a game by unpacking the player's .pkg files: pick the files, check them, confirm the folder, unpack.
    public sealed partial class MainForm
    {
        FlatButton cancelInstallButton;

        sealed class PkgStep
        {
            public Ps4PackageInfo Info;
            public string Target;
            public string Label;
        }

        bool CanInstallFromPackage(GameProfile profile)
        {
            return profile.package_install != null && string.Equals(profile.package_install.format, "ps4_pkg", StringComparison.OrdinalIgnoreCase);
        }

        string DefaultInstallFolder(GameProfile profile)
        {
            string folder = profile.package_install != null ? profile.package_install.folder : null;
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CrossCrease", profile.name);
            return Environment.ExpandEnvironmentVariables(folder.Replace("{launcher}", baseDirectory.TrimEnd(Path.DirectorySeparatorChar)));
        }

        // First step of Set up for games that can be installed from a .pkg: ask how the player has the game.
        void ChooseGameSource(LibraryItem item)
        {
            using (var dialog = new GameSourceDialog(item.Profile.name, GameFileRelative(item.Profile), item.Accent))
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                if (dialog.FromPackage) { InstallFromPackage(item); return; }
            }
            if (!ChooseGameFile(item)) return;
            item = ItemById(item.Profile.id);
            if (item != null && item.MissingPackages.Count > 0) DownloadButtonClicked(item);
        }

        // Returns true when an installation was started.
        bool InstallFromPackage(LibraryItem item)
        {
            if (item == null || installing != null || !CanInstallFromPackage(item.Profile)) return false;
            var profile = item.Profile;
            string[] paths;
            using (var dialog = new OpenFileDialog {
                Title = "Choose your " + profile.name + " .pkg files (the game, and its update if you have it)",
                Filter = "PS4 game packages (*.pkg)|*.pkg|All files (*.*)|*.*",
                Multiselect = true, CheckFileExists = true, RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return false;
                paths = dialog.FileNames;
            }

            var infos = new List<Ps4PackageInfo>();
            Cursor previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { foreach (var path in paths) infos.Add(Ps4Package.Inspect(path)); }
            finally { Cursor.Current = previous; }

            Ps4PackageInfo game = null, update = null;
            foreach (var info in infos)
            {
                string problem = PackageProblem(profile, info);
                if (problem == null)
                {
                    if (info.IsUpdate) { if (update != null) problem = "You chose more than one update. Choose only the newest one."; else update = info; }
                    else { if (game != null) problem = "You chose more than one game package. Choose one game and, if you have it, one update."; else game = info; }
                }
                if (problem != null) { PackageMessage(profile, Path.GetFileName(info.Path), problem); return false; }
            }
            if (game != null && update != null && !string.Equals(game.TitleId, update.TitleId, StringComparison.OrdinalIgnoreCase))
            {
                PackageMessage(profile, null, "The update (" + update.TitleId + ") is for a different version of the game than the game package (" + game.TitleId + "). Choose an update made for the same version.");
                return false;
            }

            var steps = new List<PkgStep>();
            string gameRoot;
            if (game != null)
            {
                string folder = DefaultInstallFolder(profile);
                using (var confirm = new PackageInstallDialog(profile.name, game, update, folder, item.Accent))
                {
                    if (confirm.ShowDialog(DialogOwner) != DialogResult.OK) return false;
                    folder = confirm.Folder;
                }
                gameRoot = Path.Combine(folder, game.TitleId);
                steps.Add(new PkgStep { Info = game, Target = gameRoot, Label = "game" });
                if (update != null) steps.Add(new PkgStep { Info = update, Target = gameRoot + "-UPDATE", Label = "update" });
            }
            else
            {
                if (!item.GameFound)
                {
                    PackageMessage(profile, Path.GetFileName(update.Path), "This is an update, and " + profile.name + " itself isn't set up yet. " +
                        "Choose the game's .pkg as well. You can select both files at once by holding Ctrl.");
                    return false;
                }
                gameRoot = item.Root.TrimEnd(Path.DirectorySeparatorChar);
                var installed = ReadParamSfo(Path.Combine(gameRoot, "sce_sys", "param.sfo"));
                string installedId;
                if (installed != null && installed.TryGetValue("TITLE_ID", out installedId) &&
                    !string.Equals(installedId, update.TitleId, StringComparison.OrdinalIgnoreCase))
                {
                    PackageMessage(profile, Path.GetFileName(update.Path), "This update is for " + update.TitleId + ", but your game is " + installedId + ". Choose an update made for your version of the game.");
                    return false;
                }
                using (var confirm = new PackageInstallDialog(profile.name, null, update, Path.GetDirectoryName(gameRoot), item.Accent))
                    if (confirm.ShowDialog(DialogOwner) != DialogResult.OK) return false;
                steps.Add(new PkgStep { Info = update, Target = gameRoot + "-UPDATE", Label = "update" });
            }

            var existing = new List<string>();
            foreach (var step in steps)
                if (Directory.Exists(step.Target) && Directory.GetFileSystemEntries(step.Target).Length > 0) existing.Add(step.Target);
            if (existing.Count > 0 && MessageBox.Show(DialogOwner, profile.name + " is already installed here:\n" + string.Join("\n", existing.ToArray()) +
                    "\n\nReplace it? Your saves and settings are kept, because CrossCrease stores them separately.",
                    "Replace " + profile.name + "?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;
            RunPackageInstall(item, steps, game != null ? gameRoot : null);
            return true;
        }

        static string PackageProblem(GameProfile profile, Ps4PackageInfo info)
        {
            if (info.Problem != null) return info.Problem;
            var ids = profile.package_install.title_ids;
            if (ids != null && ids.Length > 0 && Array.IndexOf(ids, info.TitleId.ToUpperInvariant()) < 0)
                return "This package is " + (string.IsNullOrWhiteSpace(info.Title) ? "another game" : info.Title) +
                    (string.IsNullOrWhiteSpace(info.TitleId) ? "" : " (" + info.TitleId + ")") + ", not " + profile.name + ".";
            if (info.IsAddOn) return "This package is add-on content (DLC). CrossCrease can install the game and its update.";
            if (info.IsDeltaUpdate) return "This is a partial (delta) update that only works on a console. Use the full update package instead.";
            return null;
        }

        void PackageMessage(GameProfile profile, string fileName, string message)
        {
            MessageBox.Show(DialogOwner, (fileName != null ? fileName + "\n\n" : "") + message, "Can't install " + profile.name,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void RunPackageInstall(LibraryItem item, List<PkgStep> steps, string gameRoot)
        {
            var job = new InstallJob { ProfileId = item.Profile.id, CanCancel = true, Phase = "Installing…" };
            installing = job;
            string profileId = item.Profile.id, gameName = item.Profile.name;
            long grandTotal = 0, pkgBytes = 0;
            foreach (var step in steps) { grandTotal += Math.Max(1, step.Info.InstallSize); pkgBytes += step.Info.PackageSize; }
            UpdateHero();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string failure = null;
                bool cancelled = false;
                string partial = null;
                try
                {
                    long before = 0;
                    var lastPaint = DateTime.MinValue;
                    foreach (var step in steps)
                    {
                        partial = step.Target + ".partial";
                        if (Directory.Exists(partial)) Directory.Delete(partial, true);
                        var captured = step;
                        long offset = before;
                        Ps4Package.Extract(step.Info.Path, partial, delegate(long done, long total)
                        {
                            if ((DateTime.UtcNow - lastPaint).TotalMilliseconds < 100 && done < total) return;
                            lastPaint = DateTime.UtcNow;
                            job.Fraction = Math.Min(1f, (offset + done) / (float)grandTotal);
                            job.Phase = job.CancelRequested ? "Cancelling…" : "Installing " + (int)Math.Round(job.Fraction * 100) + "%";
                            job.Detail = "Unpacking the " + captured.Label + " · " + PackageStore.FormatSize(done) + " of " + PackageStore.FormatSize(total) +
                                " · you can keep using your PC";
                            SafeInvoke(ShowProgress);
                        }, delegate { return job.CancelRequested; });
                        if (Directory.Exists(step.Target)) Directory.Delete(step.Target, true);
                        Directory.Move(partial, step.Target);
                        partial = null;
                        before += Math.Max(1, step.Info.InstallSize);
                    }
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Ps4PackageException ex) { failure = ex.Message; }
                catch (IOException ex) { failure = "The game files could not be written: " + ex.Message; }
                catch (UnauthorizedAccessException ex) { failure = "The game files could not be written: " + ex.Message; }
                catch (Exception ex) { failure = ex.Message; }
                if (partial != null) try { if (Directory.Exists(partial)) Directory.Delete(partial, true); } catch { }

                SafeInvoke(delegate
                {
                    installing = null;
                    if (failure == null && !cancelled && gameRoot != null) profileLocations[profileId] = gameRoot;
                    if (failure == null && !cancelled) SaveLocations();
                    LoadProfiles(profileId);
                    if (cancelled) return;
                    if (failure != null)
                    {
                        MessageBox.Show(DialogOwner, failure, "Could not install " + gameName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    var done = ItemById(profileId);
                    bool needsRuntime = done != null && done.MissingPackages.Count > 0;
                    MessageBox.Show(DialogOwner, gameName + " is installed in\n" + (gameRoot ?? steps[0].Target) + "\n\n" +
                        "Your .pkg files were not changed. You no longer need them to play, so you can delete them to free up " +
                        PackageStore.FormatSize(pkgBytes) + "." + (needsRuntime ? "\n\nNext, CrossCrease downloads the files it needs to run the game." : ""),
                        gameName + " installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    if (needsRuntime) DownloadButtonClicked(done);
                });
            });
        }

        // Shown next to the progress while a cancellable installation runs.
        FlatButton CancelInstallButton()
        {
            if (cancelInstallButton == null)
            {
                cancelInstallButton = new FlatButton { Kind = ButtonKind.Secondary, Text = "Cancel" };
                cancelInstallButton.Click += delegate
                {
                    var job = installing;
                    if (job == null || !job.CanCancel || job.CancelRequested) return;
                    if (MessageBox.Show(this, "Stop installing? The files unpacked so far are removed. Your .pkg files are not changed.",
                            "Stop installing", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    job.CancelRequested = true;
                    job.Phase = "Cancelling…";
                    UpdateHero();
                };
            }
            cancelInstallButton.Enabled = installing != null && !installing.CancelRequested;
            return cancelInstallButton;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            var job = installing;
            if (job != null && job.CanCancel && e.CloseReason == CloseReason.UserClosing)
            {
                if (MessageBox.Show(this, "A game is still being installed. Close CrossCrease and stop the installation?\n\nYou can start it again later.",
                        "Installation in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                job.CancelRequested = true;
            }
            base.OnFormClosing(e);
        }
    }

    // "Where is your copy of <game>?" with two large choices.
    sealed class GameSourceDialog : Form
    {
        public bool FromPackage;

        public GameSourceDialog(string gameName, string requiredFile, Color accent)
        {
            Text = "Set up " + gameName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 10f);
            int pad = Ui.S(24), width = Ui.S(460), y = pad, h = Ui.S(42);
            Controls.Add(new Label { Text = "How do you have " + gameName + "?", Font = Ui.HeavyFont(14f), AutoSize = true, Location = new Point(pad - Ui.S(2), y) });
            y += Ui.S(42);

            var package = new FlatButton { Kind = ButtonKind.Primary, Accent = accent, Text = "Install from .pkg files" };
            package.SetBounds(pad, y, width, h);
            package.Click += delegate { FromPackage = true; DialogResult = DialogResult.OK; };
            Controls.Add(package);
            y += h + Ui.S(6);
            y = AddNote(pad, y, width, "You have the game as .pkg files, for example \"UP0006-CUSA04326…pkg\". CrossCrease unpacks them into a game folder for you. Choose the game and its update together if you have both.");

            string fileName = string.IsNullOrWhiteSpace(requiredFile) ? null : Path.GetFileName(requiredFile);
            var folder = new FlatButton { Kind = ButtonKind.Secondary, Text = fileName != null ? "Select " + fileName + "…" : "Select the game folder…" };
            folder.SetBounds(pad, y, width, h);
            folder.Click += delegate { FromPackage = false; DialogResult = DialogResult.OK; };
            Controls.Add(folder);
            y += h + Ui.S(6);
            y = AddNote(pad, y, width, "The game is already unpacked into a folder" + (fileName != null ? " that contains " + fileName : "") + ".");

            var cancel = new FlatButton { Kind = ButtonKind.Ghost, Text = "Cancel" };
            int cancelWidth = cancel.PreferredWidth();
            cancel.SetBounds(pad + width - cancelWidth, y, cancelWidth, Ui.S(36));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);
            ClientSize = new Size(width + pad * 2, y + Ui.S(36) + pad);
        }

        int AddNote(int x, int y, int width, string text)
        {
            var note = new Label { Text = text, ForeColor = Ui.Muted, Location = new Point(x, y), MaximumSize = new Size(width, 0), AutoSize = true };
            Controls.Add(note);
            return y + note.PreferredHeight + Ui.S(20);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessDialogKey(keyData);
        }
    }

    // Shows what will be installed, where, and whether it fits; lets the player pick another folder.
    sealed class PackageInstallDialog : Form
    {
        public string Folder;
        readonly bool folderFixed;
        readonly long needed;
        readonly Label folderLabel, spaceLabel;
        readonly FlatButton install;

        public PackageInstallDialog(string gameName, Ps4PackageInfo game, Ps4PackageInfo update, string folder, Color accent)
        {
            Folder = folder;
            folderFixed = game == null;
            if (game != null) needed += game.InstallSize;
            if (update != null) needed += update.InstallSize;
            Text = "Install " + gameName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 10f);
            int pad = Ui.S(24), width = Ui.S(480), y = pad;
            Controls.Add(new Label { Text = game != null ? "Install " + gameName : "Install the " + gameName + " update", Font = Ui.HeavyFont(14f), AutoSize = true, Location = new Point(pad - Ui.S(2), y) });
            y += Ui.S(44);
            if (game != null) y = Line(pad, y, width, "Game", Describe(game));
            if (update != null) y = Line(pad, y, width, "Update", Describe(update));
            y += Ui.S(6);

            Controls.Add(new Label { Text = "Install to", Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), AutoSize = true, Location = new Point(pad, y) });
            y += Ui.S(24);
            folderLabel = new Label { ForeColor = Ui.Text, Location = new Point(pad, y), MaximumSize = new Size(width, 0), AutoSize = true };
            Controls.Add(folderLabel);
            y += Ui.S(26);
            if (!folderFixed)
            {
                var change = new FlatButton { Kind = ButtonKind.Secondary, Text = "Choose another folder…" };
                change.SetBounds(pad, y, change.PreferredWidth(), Ui.S(34));
                change.Click += delegate { ChangeFolder(); };
                Controls.Add(change);
                y += Ui.S(46);
            }
            spaceLabel = new Label { Location = new Point(pad, y), MaximumSize = new Size(width, 0), AutoSize = true };
            Controls.Add(spaceLabel);
            y += Ui.S(30);
            var note = new Label {
                Text = "This takes a few minutes. Your .pkg files are not changed, and you can keep using your PC while CrossCrease works.",
                ForeColor = Ui.Muted, Location = new Point(pad, y), MaximumSize = new Size(width, 0), AutoSize = true
            };
            Controls.Add(note);
            y += note.PreferredHeight + Ui.S(22);

            install = new FlatButton { Kind = ButtonKind.Primary, Accent = accent, Text = "Install" };
            var cancel = new FlatButton { Kind = ButtonKind.Secondary, Text = "Cancel" };
            int h = Ui.S(38), installWidth = Math.Max(Ui.S(120), install.PreferredWidth()), cancelWidth = cancel.PreferredWidth();
            install.SetBounds(pad + width - installWidth, y, installWidth, h);
            cancel.SetBounds(install.Left - Ui.S(10) - cancelWidth, y, cancelWidth, h);
            install.Click += delegate { if (install.Enabled) DialogResult = DialogResult.OK; };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(install);
            Controls.Add(cancel);
            ClientSize = new Size(width + pad * 2, y + h + pad);
            Refresh(Folder);
            ActiveControl = install;
        }

        static string Describe(Ps4PackageInfo info)
        {
            string title = string.IsNullOrWhiteSpace(info.Title) ? info.TitleId : info.Title + " (" + info.TitleId + ")";
            return title + (string.IsNullOrWhiteSpace(info.AppVersion) ? "" : " · version " + info.AppVersion) + " · " + PackageStore.FormatSize(info.InstallSize);
        }

        int Line(int x, int y, int width, string label, string text)
        {
            Controls.Add(new Label { Text = label, ForeColor = Ui.Muted, AutoSize = true, Location = new Point(x, y) });
            var value = new Label { Text = text, Location = new Point(x + Ui.S(70), y), MaximumSize = new Size(width - Ui.S(70), 0), AutoSize = true };
            Controls.Add(value);
            return y + Math.Max(Ui.S(24), value.PreferredHeight + Ui.S(6));
        }

        void ChangeFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose where to install the game. CrossCrease creates a game folder inside it.", ShowNewFolderButton = true })
            {
                try { if (Directory.Exists(Folder)) dialog.SelectedPath = Folder; } catch { }
                if (dialog.ShowDialog(this) == DialogResult.OK) Refresh(dialog.SelectedPath);
            }
        }

        void Refresh(string folder)
        {
            Folder = folder;
            folderLabel.Text = folder;
            long free = -1;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (!string.IsNullOrEmpty(root)) free = new DriveInfo(root).AvailableFreeSpace;
            }
            catch { }
            const long margin = 512L * 1024 * 1024;
            bool fits = free < 0 || free >= needed + margin;
            spaceLabel.Text = "Needs " + PackageStore.FormatSize(needed) + (free >= 0 ? " · " + PackageStore.FormatSize(free) + " free on this drive" : "") +
                (fits ? "" : ". Not enough space: free up space or choose a folder on another drive.");
            spaceLabel.ForeColor = fits ? Ui.Muted : Ui.Amber;
            install.Enabled = fits;
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Enter && install.Enabled) { DialogResult = DialogResult.OK; return true; }
            return base.ProcessDialogKey(keyData);
        }
    }
}
