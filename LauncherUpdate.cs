using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CrossCrease
{
    // In-place launcher updates. The startup update check reads the launcher's own feed; when a newer
    // release exists the top bar offers "Update launcher". The update is downloaded and verified like
    // any package, then swapped in: a running .exe cannot be overwritten but it can be renamed, so the
    // current exe becomes CrossCrease.exe.old (deleted on the next start), the new files are copied in
    // and the new version is started. Profiles you imported and everything in %LOCALAPPDATA% are kept.
    public sealed partial class MainForm
    {
        const string LauncherPackageId = "launcher";
        bool updatingLauncher;

        string LauncherExecutable { get { return Path.Combine(baseDirectory, "CrossCrease.exe"); } }

        // Removes the previous version left behind by an update. It may still be running for a moment.
        void CleanUpPreviousLauncher()
        {
            string old = LauncherExecutable + ".old";
            if (!File.Exists(old)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int attempt = 0; attempt < 20 && File.Exists(old); attempt++)
                {
                    try { File.Delete(old); } catch { Thread.Sleep(500); }
                }
            });
        }

        void UpdateLauncherButtonText(string text, bool enabled)
        {
            topBar.UpdateButton.Text = text;
            topBar.UpdateButton.Enabled = enabled;
            topBar.PerformLayout();
            topBar.Invalidate(true);
        }

        bool InstallFolderWritable()
        {
            try
            {
                string probe = Path.Combine(baseDirectory, ".crosscrease-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        void UpdateLauncher()
        {
            if (updatingLauncher || launcherFeed == null) return;
            var package = launcherFeed.Find(LauncherPackageId);
            if (package == null || !File.Exists(LauncherExecutable)) { OpenLauncherRelease(); return; }
            if (!InstallFolderWritable())
            {
                MessageBox.Show(DialogOwner, "CrossCrease cannot update itself because it is installed in a folder it is not allowed to change:\n\n" +
                    baseDirectory + "\n\nThe release page opens so you can download the new version yourself.",
                    "Update launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenLauncherRelease();
                return;
            }
            if (installing != null)
            {
                MessageBox.Show(DialogOwner, "Wait until the current download has finished, then update the launcher.", "Update launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string size = PackageStore.FormatSize(package.size);
            if (MessageBox.Show(DialogOwner, "CrossCrease " + launcherFeed.release + " is available (you have " + BuildInfo.Version + ").\n\n" +
                    "Download it" + (size.Length > 0 ? " (" + size + ")" : "") + " and restart the launcher now? " +
                    "Your games, downloads, saves, settings and covers are kept.",
                    "Update launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            StartLauncherUpdate(launcherFeed, package, true);
        }

        void StartLauncherUpdate(PackageFeed feed, FeedPackage package, bool restart)
        {
            updatingLauncher = true;
            UpdateLauncherButtonText("Updating…", false);
            string staging = Path.Combine(Path.GetTempPath(), "crosscrease-launcher-update-" + Guid.NewGuid().ToString("N"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                string failure = null;
                try
                {
                    packages.DownloadAndExtract(feed, package, staging, delegate(long done, long total, string phase)
                    {
                        string text = phase == "Downloading" && total > 0 ? "Updating " + (int)(100 * done / total) + "%" : phase + "…";
                        SafeInvoke(delegate { UpdateLauncherButtonText(text, false); });
                    });
                }
                catch (Exception ex) { failure = FriendlyError(ex); }
                SafeInvoke(delegate
                {
                    try
                    {
                        if (failure == null) ApplyLauncherUpdate(staging);
                    }
                    catch (Exception ex) { failure = "The update could not be installed: " + ex.Message; }
                    finally
                    {
                        try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                    }
                    updatingLauncher = false;
                    if (failure != null)
                    {
                        UpdateLauncherButton();
                        MessageBox.Show(DialogOwner, failure, "Update launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    if (restart)
                    {
                        try { Process.Start(new ProcessStartInfo(LauncherExecutable) { WorkingDirectory = baseDirectory, UseShellExecute = false }); }
                        catch (Exception ex)
                        {
                            MessageBox.Show(DialogOwner, "The update is installed, but the new version could not be started: " + ex.Message +
                                "\n\nStart CrossCrease again.", "Update launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        Close();
                    }
                });
            });
        }

        // Copies the release's files over this installation. Release ZIPs contain a top-level
        // "CrossCrease" folder; files that are not in the release (imported profiles) are left alone.
        void ApplyLauncherUpdate(string staging)
        {
            string source = Path.Combine(staging, "CrossCrease");
            if (!Directory.Exists(source)) source = staging;
            string newExe = Path.Combine(source, "CrossCrease.exe");
            if (!File.Exists(newExe)) throw new PackageException("The update does not contain CrossCrease.exe.");

            string exe = LauncherExecutable, old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Copy(newExe, exe);
            }
            catch
            {
                if (!File.Exists(exe)) File.Move(old, exe);
                throw;
            }

            string root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(root.Length);
                if (string.Equals(relative, "CrossCrease.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(baseDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }
    }
}
