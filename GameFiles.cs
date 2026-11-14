using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CrossCrease
{
    // Choosing where a game's files are: pick the game's main file (for example eboot.bin) or its folder.
    // The choice is stored per user and per game in %LOCALAPPDATA%\CrossCrease\locations.json.
    public sealed partial class MainForm
    {
        // Relative path of the file that identifies the game inside its folder ("eboot.bin" for PS4 titles).
        static string GameFileRelative(GameProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.required_file_relative)) return profile.required_file_relative;
            if (!string.IsNullOrWhiteSpace(profile.executable_relative)) return profile.executable_relative;
            return null;
        }

        bool HasSavedLocation(GameProfile profile)
        {
            return profileLocations.ContainsKey(profile.id);
        }

        void LocateGame(LibraryItem item)
        {
            if (item == null || item.State == GameState.Planned) return;
            Control anchor = null;
            if (library.SelectedItem == item)
                anchor = item.State == GameState.Missing && hero.PrimaryButton.Visible ? hero.PrimaryButton : null;
            var screen = anchor != null ? anchor.PointToScreen(new Point(0, anchor.Height + Ui.S(4))) : Cursor.Position;
            menu.Items.Clear();
            foreach (var entry in GameFilesMenuItems(item)) menu.Items.Add(entry);
            menu.Show(screen);
        }

        // Shared by the selection bar and the cover's right-click menu.
        List<ToolStripItem> GameFilesMenuItems(LibraryItem item)
        {
            var entries = new List<ToolStripItem>();
            string relative = GameFileRelative(item.Profile);
            if (relative != null)
                entries.Add(MenuEntry("Select " + Path.GetFileName(relative) + "…", delegate { ChooseGameFile(item); }));
            entries.Add(MenuEntry("Select game folder…", delegate { ChooseGameFolder(item); }));
            if (CanInstallFromPackage(item.Profile))
            {
                var install = MenuEntry("Install from .pkg files…", delegate { InstallFromPackage(item); });
                install.Enabled = installing == null;
                entries.Add(install);
            }
            var reset = MenuEntry("Use automatic detection", delegate { ResetGameLocation(item); });
            reset.Enabled = HasSavedLocation(item.Profile);
            entries.Add(reset);
            return entries;
        }

        ToolStripMenuItem MenuEntry(string text, EventHandler handler)
        {
            var entry = new ToolStripMenuItem(text) { ForeColor = Ui.Text, Padding = new Padding(4, 3, 4, 3) };
            entry.Click += handler;
            return entry;
        }

        bool ChooseGameFile(LibraryItem item)
        {
            var profile = item.Profile;
            string relative = GameFileRelative(profile);
            if (relative == null) return ChooseGameFolder(item);
            string fileName = Path.GetFileName(relative);
            using (var dialog = new OpenFileDialog {
                Title = "Select " + fileName + " for " + profile.name,
                Filter = fileName + "|" + fileName + "|All files (*.*)|*.*",
                CheckFileExists = true,
                RestoreDirectory = true
            })
            {
                if (!string.IsNullOrWhiteSpace(item.Root) && Directory.Exists(item.Root)) dialog.InitialDirectory = item.Root;
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return false;
                string root = RootFromFile(profile, dialog.FileName);
                if (root == null)
                {
                    MessageBox.Show(DialogOwner, "That file does not belong to a " + profile.name + " game folder.\n\nCrossCrease expects " +
                        relative + " inside the base game folder, next to the game's other files. An update belongs in a folder " +
                        "named like the game folder plus \"-UPDATE\", right next to it.", "Game not found",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                SaveGameLocation(item, root);
                return true;
            }
        }

        bool ChooseGameFolder(LibraryItem item)
        {
            var profile = item.Profile;
            using (var dialog = new FolderBrowserDialog {
                Description = "Select the folder that contains " + profile.name + (GameFileRelative(profile) != null ? " (" + Path.GetFileName(GameFileRelative(profile)) + ")" : ""),
                ShowNewFolderButton = false
            })
            {
                if (!string.IsNullOrWhiteSpace(item.Root) && Directory.Exists(item.Root)) dialog.SelectedPath = item.Root;
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return false;
                string root = RootFromFolder(profile, dialog.SelectedPath);
                if (root == null)
                {
                    string expected = GameFileRelative(profile) ?? "the configured game files";
                    MessageBox.Show(DialogOwner, "CrossCrease could not find " + expected + " in that folder or the folders inside it.\n\n" +
                        "Select the game folder, or use Select " + Path.GetFileName(expected) + "… to point at the file directly.",
                        "Game not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                SaveGameLocation(item, root);
                return true;
            }
        }

        // The game folder is the chosen file's path minus the profile's relative path to that file.
        string RootFromFile(GameProfile profile, string file)
        {
            string relative = GameFileRelative(profile);
            if (relative == null) return null;
            string full = Path.GetFullPath(file);
            string suffix = Path.DirectorySeparatorChar + relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (!full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
            string root = full.Substring(0, full.Length - suffix.Length);
            if (root.EndsWith(":")) root += Path.DirectorySeparatorChar;
            return ProfileValidAtRoot(profile, root) ? BaseGameFolder(profile, root) : null;
        }

        // Accepts the game folder itself or a folder up to three levels above it (for example a drive or
        // a "Games" folder), preferring the shallowest match and skipping update/patch overlay folders.
        string RootFromFolder(GameProfile profile, string folder)
        {
            if (ProfileValidAtRoot(profile, folder)) return BaseGameFolder(profile, folder);
            var level = new List<string> { folder };
            for (int depth = 0; depth < 3 && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var parent in level)
                {
                    string[] children;
                    try { children = Directory.GetDirectories(parent); } catch { continue; }
                    Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                    foreach (var child in children)
                    {
                        string name = Path.GetFileName(child);
                        if (IsOverlayFolder(name)) continue;
                        if (ProfileValidAtRoot(profile, child)) return child;
                        next.Add(child);
                    }
                }
                level = next;
            }
            return null;
        }

        // An update ("-UPDATE"/"-patch") folder also contains eboot.bin, but shadPS4 must be pointed at the
        // base game next to it; it then applies the update itself. Returns null if there is no base game.
        string BaseGameFolder(GameProfile profile, string root)
        {
            string trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
            foreach (var suffix in new[] { "-UPDATE", "-patch" })
            {
                if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                string baseFolder = trimmed.Substring(0, trimmed.Length - suffix.Length);
                return ProfileValidAtRoot(profile, baseFolder) ? baseFolder : null;
            }
            return root;
        }

        static bool IsOverlayFolder(string name)
        {
            return name.EndsWith("-UPDATE", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-patch", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-mods", StringComparison.OrdinalIgnoreCase);
        }

        void SaveGameLocation(LibraryItem item, string root)
        {
            profileLocations[item.Profile.id] = root;
            SaveLocations();
            LoadProfiles(item.Profile.id);
        }

        void ResetGameLocation(LibraryItem item)
        {
            if (!profileLocations.Remove(item.Profile.id)) return;
            SaveLocations();
            LoadProfiles(item.Profile.id);
        }

        // ----- Game version (PS4 param.sfo) ------------------------------------------------------

        static string Join(string a, string b)
        {
            return string.IsNullOrWhiteSpace(a) ? b : a + " · " + b;
        }

        static Dictionary<string, string> ReadParamSfo(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
                return ParseParamSfo(File.ReadAllBytes(path));
            }
            catch { return null; }
        }

        internal static Dictionary<string, string> ParseParamSfo(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 20 || data[0] != 0 || data[1] != (byte)'P' || data[2] != (byte)'S' || data[3] != (byte)'F') return null;
                int keyTable = BitConverter.ToInt32(data, 8), dataTable = BitConverter.ToInt32(data, 12), count = BitConverter.ToInt32(data, 16);
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count && 20 + i * 16 + 16 <= data.Length; i++)
                {
                    int entry = 20 + i * 16;
                    int keyOffset = BitConverter.ToUInt16(data, entry), format = BitConverter.ToUInt16(data, entry + 2);
                    int length = BitConverter.ToInt32(data, entry + 4), offset = BitConverter.ToInt32(data, entry + 12);
                    int keyStart = keyTable + keyOffset, keyEnd = keyStart;
                    while (keyEnd < data.Length && data[keyEnd] != 0) keyEnd++;
                    string key = Encoding.ASCII.GetString(data, keyStart, keyEnd - keyStart);
                    int start = dataTable + offset;
                    if (start < 0 || start + length > data.Length) continue;
                    if (format == 0x0204 || format == 0x0004)
                        values[key] = Encoding.UTF8.GetString(data, start, length).TrimEnd('\0');
                    else if (format == 0x0404 && length >= 4)
                        values[key] = BitConverter.ToInt32(data, start).ToString();
                }
                return values;
            }
            catch { return null; }
        }
    }
}
