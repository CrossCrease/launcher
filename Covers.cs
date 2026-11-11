using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CrossCrease
{
    // Where a profile's official cover can be downloaded from. Covers are fetched on the player's PC
    // and stored per user; they are never part of the repository or a release.
    public sealed class CoverDownload
    {
        // "wikipedia": an English Wikipedia file name in "file". "url": a direct HTTPS image URL in "url".
        public string source { get; set; }
        public string file { get; set; }
        public string url { get; set; }
    }

    public sealed class DownloadedCover
    {
        public string key { get; set; }
        public string file { get; set; }
        public string sha1 { get; set; }
        public string downloaded { get; set; }
    }

    // Downloads covers listed in profiles into %LOCALAPPDATA%\CrossCrease\covers\downloaded\.
    // A cover the player chose themselves always wins over a downloaded one.
    public sealed partial class MainForm
    {
        const string CoverIndexName = "index.json";
        static readonly TimeSpan CoverCheckInterval = TimeSpan.FromDays(1);
        bool syncingCovers;

        string DownloadedCoversDirectory { get { return Path.Combine(coversDirectory, "downloaded"); } }

        // Turned off with { "download_covers": false } in %LOCALAPPDATA%\CrossCrease\launcher.json.
        bool CoverDownloadsEnabled()
        {
            try
            {
                string file = Path.Combine(dataDirectory, "launcher.json");
                if (!File.Exists(file)) return true;
                var settings = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                object value;
                return settings == null || !settings.TryGetValue("download_covers", out value) || !(value is bool) || (bool)value;
            }
            catch { return true; }
        }

        static string CoverKey(CoverDownload cover)
        {
            if (cover == null) return null;
            if (string.Equals(cover.source, "wikipedia", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cover.file))
                return "wikipedia:" + cover.file.Trim().Replace('_', ' ');
            if (string.Equals(cover.source, "url", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cover.url) &&
                cover.url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "url:" + cover.url.Trim();
            return null;
        }

        Dictionary<string, DownloadedCover> LoadCoverIndex(out DateTime lastCheck)
        {
            lastCheck = DateTime.MinValue;
            var index = new Dictionary<string, DownloadedCover>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string file = Path.Combine(DownloadedCoversDirectory, CoverIndexName);
                if (!File.Exists(file)) return index;
                var data = new JavaScriptSerializer().Deserialize<CoverIndexFile>(File.ReadAllText(file));
                if (data == null) return index;
                DateTime.TryParse(data.last_check, null, System.Globalization.DateTimeStyles.RoundtripKind, out lastCheck);
                if (data.covers != null) foreach (var pair in data.covers) index[pair.Key] = pair.Value;
            }
            catch { }
            return index;
        }

        sealed class CoverIndexFile
        {
            public string last_check { get; set; }
            public Dictionary<string, DownloadedCover> covers { get; set; }
        }

        void SaveCoverIndex(Dictionary<string, DownloadedCover> index, DateTime lastCheck)
        {
            Directory.CreateDirectory(DownloadedCoversDirectory);
            string file = Path.Combine(DownloadedCoversDirectory, CoverIndexName);
            File.WriteAllText(file + ".tmp", new JavaScriptSerializer().Serialize(new CoverIndexFile {
                last_check = lastCheck.ToUniversalTime().ToString("o"), covers = index }));
            if (File.Exists(file)) File.Delete(file);
            File.Move(file + ".tmp", file);
        }

        // The downloaded cover for this profile, if it matches the profile's current cover source.
        string DownloadedCoverPath(GameProfile profile)
        {
            string key = CoverKey(profile.cover_download);
            if (key == null) return null;
            DateTime ignored;
            DownloadedCover entry;
            if (!LoadCoverIndex(out ignored).TryGetValue(profile.id, out entry) || entry.key != key || string.IsNullOrWhiteSpace(entry.file)) return null;
            string path = Path.Combine(DownloadedCoversDirectory, Path.GetFileName(entry.file));
            return File.Exists(path) ? path : null;
        }

        string DownloadedCoverDescription(GameProfile profile)
        {
            if (DownloadedCoverPath(profile) == null || profile.cover_download == null) return null;
            return string.Equals(profile.cover_download.source, "wikipedia", StringComparison.OrdinalIgnoreCase)
                ? "the official cover, downloaded from Wikipedia" : "the official cover, downloaded from the profile's cover address";
        }

        // Runs with the startup update check. Checks sources at most once a day unless a cover is missing,
        // downloads one image at a time (Wikimedia rate-limits fast clients), and verifies each file.
        void BeginCoverSync(bool force)
        {
            if (syncingCovers || !CoverDownloadsEnabled()) return;
            var wanted = new Dictionary<string, CoverDownload>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                if (CoverKey(item.Profile.cover_download) != null && UserCoverPath(item.Profile) == null)
                    wanted[item.Profile.id] = item.Profile.cover_download;
            if (wanted.Count == 0) return;

            DateTime lastCheck;
            var index = LoadCoverIndex(out lastCheck);
            bool missing = false;
            foreach (var pair in wanted)
            {
                DownloadedCover entry;
                if (!index.TryGetValue(pair.Key, out entry) || entry.key != CoverKey(pair.Value) ||
                    !File.Exists(Path.Combine(DownloadedCoversDirectory, Path.GetFileName(entry.file ?? "")))) missing = true;
            }
            if (!force && !missing && DateTime.UtcNow - lastCheck < CoverCheckInterval) return;

            syncingCovers = true;
            string directory = DownloadedCoversDirectory;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int changed = 0;
                try { changed = SyncCovers(wanted, index, directory); }
                catch { }
                SafeInvoke(delegate
                {
                    syncingCovers = false;
                    if (changed > 0)
                    {
                        var selected = library.SelectedItem;
                        LoadProfiles(selected == null ? null : selected.Profile.id);
                    }
                });
            });
        }

        int SyncCovers(Dictionary<string, CoverDownload> wanted, Dictionary<string, DownloadedCover> index, string directory)
        {
            Directory.CreateDirectory(directory);
            // Resolve all Wikipedia files in one API request: current URL and SHA-1 of each image.
            var wikipediaFiles = new List<string>();
            foreach (var cover in wanted.Values)
                if (string.Equals(cover.source, "wikipedia", StringComparison.OrdinalIgnoreCase)) wikipediaFiles.Add(cover.file.Trim().Replace('_', ' '));
            var resolved = ResolveWikipediaFiles(wikipediaFiles);

            int changed = 0;
            bool first = true;
            foreach (var pair in wanted)
            {
                string key = CoverKey(pair.Value), url, sha1 = null;
                if (key.StartsWith("wikipedia:"))
                {
                    string[] info;
                    if (!resolved.TryGetValue(key.Substring("wikipedia:".Length), out info)) continue;
                    url = info[0];
                    sha1 = info[1];
                }
                else url = pair.Value.url.Trim();

                DownloadedCover entry;
                string existing = index.TryGetValue(pair.Key, out entry) ? Path.Combine(directory, Path.GetFileName(entry.file ?? "")) : null;
                if (entry != null && entry.key == key && File.Exists(existing) && (sha1 == null || string.Equals(entry.sha1, sha1, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!first) Thread.Sleep(1500);
                first = false;
                byte[] data = DownloadCoverImage(url);
                if (data == null) continue;
                string actual = Sha1(data);
                if (sha1 != null && !string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase)) continue;
                string extension = ImageExtension(data);
                if (extension == null) continue;

                string name = SafeFileName(pair.Key) + extension;
                string target = Path.Combine(directory, name);
                File.WriteAllBytes(target + ".tmp", data);
                if (File.Exists(target)) File.Delete(target);
                File.Move(target + ".tmp", target);
                if (existing != null && File.Exists(existing) && !string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(existing); } catch { }
                index[pair.Key] = new DownloadedCover { key = key, file = name, sha1 = actual, downloaded = DateTime.UtcNow.ToString("o") };
                SaveCoverIndex(index, DateTime.UtcNow);
                changed++;
            }
            SaveCoverIndex(index, DateTime.UtcNow);
            return changed;
        }

        // Returns file name -> { image URL, SHA-1 } for files that exist on English Wikipedia.
        static Dictionary<string, string[]> ResolveWikipediaFiles(List<string> files)
        {
            var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            for (int start = 0; start < files.Count; start += 40)
            {
                var titles = new List<string>();
                for (int i = start; i < Math.Min(files.Count, start + 40); i++) titles.Add("File:" + files[i]);
                string api = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&prop=imageinfo&iiprop=url%7Csha1%7Cmime&titles=" +
                    Uri.EscapeDataString(string.Join("|", titles.ToArray()));
                byte[] response = DownloadCoverImage(api);
                if (response == null) continue;
                var root = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(response)) as Dictionary<string, object>;
                object query, pages;
                if (root == null || !root.TryGetValue("query", out query) || !((Dictionary<string, object>)query).TryGetValue("pages", out pages)) continue;
                foreach (Dictionary<string, object> page in (object[])pages)
                {
                    object title, infos;
                    if (!page.TryGetValue("title", out title) || !page.TryGetValue("imageinfo", out infos)) continue;
                    var info = ((object[])infos)[0] as Dictionary<string, object>;
                    object url, sha1, mime;
                    if (info == null || !info.TryGetValue("url", out url) || !info.TryGetValue("sha1", out sha1)) continue;
                    if (info.TryGetValue("mime", out mime) && !((string)mime).StartsWith("image/")) continue;
                    string name = ((string)title).Substring(((string)title).IndexOf(':') + 1);
                    result[name] = new[] { (string)url, (string)sha1 };
                }
            }
            return result;
        }

        // HTTPS GET with the launcher's User-Agent (required by Wikimedia) and one polite retry on HTTP 429.
        static byte[] DownloadCoverImage(string url)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    if (request.RequestUri.Scheme != Uri.UriSchemeHttps) return null;
                    request.UserAgent = PackageStore.UserAgent;
                    request.Timeout = 20000;
                    request.ReadWriteTimeout = 30000;
                    using (var response = request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var memory = new MemoryStream())
                    {
                        var buffer = new byte[16384];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            memory.Write(buffer, 0, read);
                            if (memory.Length > 8 * 1024 * 1024) return null;
                        }
                        return memory.ToArray();
                    }
                }
                catch (WebException ex)
                {
                    var http = ex.Response as HttpWebResponse;
                    if (http == null || (int)http.StatusCode != 429 || attempt > 0) return null;
                    int wait;
                    if (!int.TryParse(http.Headers["Retry-After"], out wait)) wait = 20;
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Min(60, Math.Max(5, wait))));
                }
                catch { return null; }
            }
            return null;
        }

        static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        // Only real JPEG/PNG images that decode are stored.
        static string ImageExtension(byte[] data)
        {
            string extension = null;
            if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) extension = ".jpg";
            else if (data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G') extension = ".png";
            if (extension == null) return null;
            try
            {
                using (var stream = new MemoryStream(data))
                using (var image = Image.FromStream(stream))
                    return image.Width >= 32 && image.Height >= 32 ? extension : null;
            }
            catch { return null; }
        }
    }
}
