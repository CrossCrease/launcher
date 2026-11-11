using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace CrossCrease
{
    // A per-game on/off choice that adds launch arguments, for example "use the game update".
    public sealed class LaunchToggle
    {
        public string id { get; set; }
        public string label { get; set; }
        public string description { get; set; }
        public bool @default { get; set; }
        // "game_update": only offered when an update ("<game folder>-UPDATE" / "-patch") is next to the game.
        public string requires { get; set; }
        public string arguments_on { get; set; }
        public string arguments_off { get; set; }
        // Launches it applies to: "play" is the main Play button, other values are launch_options ids.
        public string[] launches { get; set; }
    }

    // Setting a game up (your game files first, then the CrossCrease downloads) and per-game toggles.
    public sealed partial class MainForm
    {
        // ----- Set up ----------------------------------------------------------------------------

        void ShowSetupHero(LibraryItem item)
        {
            var primary = hero.PrimaryButton;
            primary.Accent = item.Accent;
            primary.Enabled = true;
            bool needFiles = !item.GameFound, needDownload = item.MissingPackages.Count > 0;

            PackageFeed feed = null;
            string downloadProblem = null;
            bool checking = false, launcherTooOld = false;
            if (needDownload)
            {
                string error;
                if (FeedUrl(item.Profile) == null) downloadProblem = "not available for this profile";
                else if (!feeds.TryGetValue(item.Profile.id, out feed))
                {
                    if (checkingFeeds) checking = true;
                    else downloadProblem = feedErrors.TryGetValue(item.Profile.id, out error) ? error : "the download list is not available";
                }
                else if (LauncherTooOld(feed)) { launcherTooOld = true; downloadProblem = "needs CrossCrease launcher " + feed.minimum_launcher + " or newer"; }
            }
            long size = feed != null ? DownloadSize(feed, item.MissingPackages) : 0;

            string filesStep = item.GameFound ? "Game files found" : "Select your game files";
            string runtimeStep = !needDownload ? "CrossCrease runtime installed"
                : checking ? "CrossCrease runtime: checking for the download…"
                : downloadProblem != null ? "CrossCrease runtime: " + downloadProblem
                : WithSize("Download the CrossCrease runtime", size);
            hero.Detail = "1. " + filesStep + "    2. " + runtimeStep;

            if (needFiles) primary.Text = "Set up " + item.Profile.name;
            else if (checking) { primary.Text = "Checking…"; primary.Enabled = false; }
            else if (launcherTooOld) primary.Text = "Get launcher update";
            else if (downloadProblem != null) { primary.Text = FeedUrl(item.Profile) == null ? "Download unavailable" : "Try again"; primary.Enabled = FeedUrl(item.Profile) != null; }
            else primary.Text = WithSize("Download runtime", size);
            hero.SetButtons(primary, hero.SettingsButton);
        }

        // Game files first (skipped when they were found automatically), then the downloads.
        void RunSetup(LibraryItem item)
        {
            if (item == null || installing != null) return;
            string id = item.Profile.id;
            if (!item.GameFound)
            {
                if (!ChooseGameFile(item)) return;
                item = ItemById(id);
                if (item == null) return;
            }
            if (item.MissingPackages.Count > 0) DownloadButtonClicked(item);
        }

        // ----- Toggles ---------------------------------------------------------------------------

        IEnumerable<LaunchToggle> Toggles(GameProfile profile)
        {
            if (profile.toggles == null) yield break;
            foreach (var toggle in profile.toggles)
                if (toggle != null && !string.IsNullOrWhiteSpace(toggle.id)) yield return toggle;
        }

        bool ToggleValue(GameProfile profile, LaunchToggle toggle)
        {
            string value;
            if (LoadSettings(profile).TryGetValue("toggle:" + toggle.id, out value))
            {
                bool parsed;
                if (bool.TryParse(value, out parsed)) return parsed;
            }
            return toggle.@default;
        }

        void SetToggle(LibraryItem item, LaunchToggle toggle, bool value)
        {
            var values = LoadSettings(item.Profile);
            values["toggle:" + toggle.id] = value ? "true" : "false";
            try { SaveSettings(item.Profile, values); }
            catch (Exception ex) { MessageBox.Show(DialogOwner, ex.Message, "Could not save setting", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            UpdateHero();
        }

        bool ToggleAvailable(LibraryItem item, LaunchToggle toggle)
        {
            if (string.Equals(toggle.requires, "game_update", StringComparison.OrdinalIgnoreCase))
                return item.GameFound && GameUpdateVersion(item.Root) != null;
            return true;
        }

        string ToggleArguments(LibraryItem item, string launchId)
        {
            var parts = new List<string>();
            foreach (var toggle in Toggles(item.Profile))
            {
                bool applies = toggle.launches == null || toggle.launches.Length == 0
                    ? launchId == "play" : Array.IndexOf(toggle.launches, launchId) >= 0;
                if (!applies || !ToggleAvailable(item, toggle)) continue;
                string arguments = ToggleValue(item.Profile, toggle) ? toggle.arguments_on : toggle.arguments_off;
                if (!string.IsNullOrWhiteSpace(arguments)) parts.Add(arguments.Trim());
            }
            return parts.Count == 0 ? "" : " " + string.Join(" ", parts.ToArray());
        }

        LaunchToggle UpdateToggle(GameProfile profile)
        {
            foreach (var toggle in Toggles(profile))
                if (string.Equals(toggle.requires, "game_update", StringComparison.OrdinalIgnoreCase)) return toggle;
            return null;
        }

        void AddToggleMenuItems(LibraryItem item)
        {
            foreach (var toggle in Toggles(item.Profile))
            {
                if (!ToggleAvailable(item, toggle)) continue;
                var captured = toggle;
                bool value = ToggleValue(item.Profile, toggle);
                var entry = AddMenuItem(toggle.label ?? toggle.id, delegate { SetToggle(item, captured, !value); });
                entry.Checked = value;
            }
        }

        // ----- Game version (PS4 param.sfo) ------------------------------------------------------

        // Version of the update that shadPS4 would apply: "<game folder>-UPDATE" or "<game folder>-patch".
        static string GameUpdateVersion(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            string trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
            foreach (var suffix in new[] { "-UPDATE", "-patch" })
            {
                var update = ReadParamSfo(Path.Combine(trimmed + suffix, "sce_sys", "param.sfo"));
                string version;
                if (update != null && update.TryGetValue("APP_VER", out version) && !string.IsNullOrWhiteSpace(version)) return version;
            }
            return null;
        }

        // e.g. "CUSA04326 · version 01.08 (update)" or "CUSA04326 · base game 01.00". The update is optional.
        string GameVersionText(LibraryItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Root)) return null;
            var info = ReadParamSfo(Path.Combine(item.Root, "sce_sys", "param.sfo"));
            if (info == null) return null;
            string titleId, baseVersion;
            info.TryGetValue("TITLE_ID", out titleId);
            info.TryGetValue("APP_VER", out baseVersion);
            string baseText = "base game" + (string.IsNullOrWhiteSpace(baseVersion) ? "" : " " + baseVersion);
            string update = GameUpdateVersion(item.Root);
            if (update == null) return Join(titleId, baseText);
            var toggle = UpdateToggle(item.Profile);
            bool useUpdate = toggle == null || ToggleValue(item.Profile, toggle);
            return Join(titleId, useUpdate ? "version " + update + " (update)" : baseText + " (update " + update + " turned off)");
        }
    }
}
