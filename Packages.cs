using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CrossCrease
{
    // A package feed is a small JSON file published next to a game project's release assets.
    // See README.md ("Downloads and updates") for the format.
    public sealed class PackageFeed
    {
        public int schema_version { get; set; }
        public string game { get; set; }
        public string release { get; set; }
        public bool prerelease { get; set; }
        public string published { get; set; }
        public string notes_url { get; set; }
        public string source_url { get; set; }
        public string minimum_launcher { get; set; }
        public List<FeedPackage> packages { get; set; }

        [ScriptIgnore] public Uri BaseUri { get; set; }

        public FeedPackage Find(string id)
        {
            if (packages == null) return null;
            foreach (var package in packages)
                if (string.Equals(package.id, id, StringComparison.OrdinalIgnoreCase)) return package;
            return null;
        }
    }

    public sealed class FeedPackage
    {
        public string id { get; set; }
        public string name { get; set; }
        public string version { get; set; }
        public string description { get; set; }
        public string url { get; set; }
        public long size { get; set; }
        public string sha256 { get; set; }
    }

    public sealed class InstalledPackage
    {
        public string version { get; set; }
        public string previous { get; set; }
        public string sha256 { get; set; }
        public string previous_sha256 { get; set; }
        public string installed { get; set; }
    }

    public sealed class PackageException : Exception
    {
        public PackageException(string message) : base(message) { }
    }

    // Downloads, verifies and installs game packages under
    // %LOCALAPPDATA%\CrossCrease\games\<game id>\packages\<package id>\<version>\.
    // Player data lives in the game's data folder next to "packages" and is never touched here.
    public sealed class PackageStore
    {
        static readonly Regex SafeName = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");
        readonly string gamesDirectory;
        readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public PackageStore(string dataDirectory)
        {
            gamesDirectory = Path.Combine(dataDirectory, "games");
        }

        static PackageStore()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // TLS 1.2
        }

        // Includes the project address, as Wikimedia's User-Agent policy asks of API clients.
        public static string UserAgent { get { return "CrossCrease-Launcher/" + BuildInfo.Version + " (https://github.com/CrossCrease/launcher)"; } }

        public string GameDirectory(string gameId)
        {
            if (!SafeName.IsMatch(gameId ?? "")) throw new PackageException("Invalid game id: " + gameId);
            return Path.Combine(gamesDirectory, gameId);
        }

        string PackagesDirectory(string gameId) { return Path.Combine(GameDirectory(gameId), "packages"); }
        string RegistryFile(string gameId) { return Path.Combine(PackagesDirectory(gameId), "installed.json"); }

        public Dictionary<string, InstalledPackage> Installed(string gameId)
        {
            try
            {
                string file = RegistryFile(gameId);
                if (File.Exists(file))
                {
                    var registry = serializer.Deserialize<Dictionary<string, InstalledPackage>>(File.ReadAllText(file));
                    if (registry != null) return new Dictionary<string, InstalledPackage>(registry, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return new Dictionary<string, InstalledPackage>(StringComparer.OrdinalIgnoreCase);
        }

        // Serialises registry changes between the UI thread and download threads.
        static readonly object RegistryLock = new object();

        // The installed entry for a package, repaired when installed.json no longer matches the folders
        // (for example after a version folder was deleted by hand): it falls back to the kept previous
        // version, or to the newest remaining version folder. Returns null when nothing usable is left.
        InstalledPackage Resolve(string gameId, string packageId)
        {
            if (!SafeName.IsMatch(packageId ?? "")) return null;
            lock (RegistryLock)
            {
                var registry = Installed(gameId);
                InstalledPackage entry;
                registry.TryGetValue(packageId, out entry);
                string packageDir = Path.Combine(PackagesDirectory(gameId), packageId);
                var versions = VersionFolders(packageDir);
                if (entry != null && SafeName.IsMatch(entry.version ?? "") && versions.Contains(entry.version))
                {
                    if (entry.previous != null && !versions.Contains(entry.previous))
                    {
                        entry.previous = null;
                        entry.previous_sha256 = null;
                        TryWriteRegistry(gameId, registry);
                    }
                    return entry;
                }
                if (versions.Count == 0)
                {
                    if (entry != null && registry.Remove(packageId)) TryWriteRegistry(gameId, registry);
                    return null;
                }
                bool usePrevious = entry != null && entry.previous != null && versions.Contains(entry.previous);
                string chosen = usePrevious ? entry.previous : versions[0];
                string other = null;
                foreach (var version in versions) if (version != chosen) { other = version; break; }
                var healed = new InstalledPackage {
                    version = chosen, sha256 = usePrevious ? entry.previous_sha256 : null,
                    previous = other, installed = DateTime.UtcNow.ToString("o")
                };
                registry[packageId] = healed;
                TryWriteRegistry(gameId, registry);
                return healed;
            }
        }

        // Non-empty version folders of a package, newest first. Staging folders start with a dot.
        static List<string> VersionFolders(string packageDir)
        {
            var versions = new List<string>();
            try
            {
                if (!Directory.Exists(packageDir)) return versions;
                foreach (var folder in Directory.GetDirectories(packageDir))
                {
                    string name = Path.GetFileName(folder);
                    if (SafeName.IsMatch(name) && Directory.GetFileSystemEntries(folder).Length > 0) versions.Add(name);
                }
            }
            catch { }
            versions.Sort(delegate(string a, string b) { return CompareVersions(b, a); });
            return versions;
        }

        // Folder of the installed package, or null when it is not installed (or its files are gone).
        public string InstalledPath(string gameId, string packageId)
        {
            var entry = Resolve(gameId, packageId);
            return entry == null ? null : Path.Combine(PackagesDirectory(gameId), packageId, entry.version);
        }

        public string InstalledVersion(string gameId, string packageId)
        {
            var entry = Resolve(gameId, packageId);
            return entry == null ? null : entry.version;
        }

        // The version kept for rollback, if its files are still there.
        public string PreviousVersion(string gameId, string packageId)
        {
            var entry = Resolve(gameId, packageId);
            return entry == null ? null : entry.previous;
        }

        // Switches to the kept previous version. The current one stays on disk, so going forward again
        // (Update) does not need a new download.
        public void RollBack(string gameId, string packageId)
        {
            lock (RegistryLock)
            {
                var entry = Resolve(gameId, packageId);
                if (entry == null || entry.previous == null) throw new PackageException("There is no earlier version to go back to.");
                var registry = Installed(gameId);
                registry[packageId] = new InstalledPackage {
                    version = entry.previous, sha256 = entry.previous_sha256,
                    previous = entry.version, previous_sha256 = entry.sha256, installed = DateTime.UtcNow.ToString("o")
                };
                WriteRegistry(gameId, registry);
            }
        }

        void TryWriteRegistry(string gameId, Dictionary<string, InstalledPackage> registry)
        {
            try { WriteRegistry(gameId, registry); } catch { }
        }

        public bool HasPackages(string gameId)
        {
            try { return Directory.Exists(PackagesDirectory(gameId)); } catch { return false; }
        }

        // ----- Feed ----------------------------------------------------------------------

        public PackageFeed FetchFeed(string url)
        {
            Uri responseUri;
            string text = Encoding.UTF8.GetString(Download(new Uri(url), out responseUri));
            var feed = serializer.Deserialize<PackageFeed>(text);
            if (feed == null || feed.schema_version != 1 || feed.packages == null)
                throw new PackageException("The download list has an unsupported format.");
            foreach (var package in feed.packages)
            {
                if (!SafeName.IsMatch(package.id ?? "") || !SafeName.IsMatch(package.version ?? "") ||
                    string.IsNullOrWhiteSpace(package.url) || !Regex.IsMatch(package.sha256 ?? "", "^[0-9A-Fa-f]{64}$"))
                    throw new PackageException("The download list contains an invalid package entry.");
            }
            feed.BaseUri = responseUri;
            return feed;
        }

        // Follows redirects itself so relative package URLs can be resolved against the last address on
        // the feed's own host. GitHub sends releases/latest/download/... to releases/download/<tag>/...
        // (same host, the right base) and then to a short-lived signed storage URL (wrong base).
        static WebResponse Open(Uri uri, int timeout, out Uri baseUri)
        {
            baseUri = uri;
            for (int hop = 0; hop < 10; hop++)
            {
                var request = WebRequest.Create(uri);
                request.Timeout = timeout;
                var http = request as HttpWebRequest;
                if (http == null) return request.GetResponse();
                http.UserAgent = UserAgent;
                http.AllowAutoRedirect = false;
                http.ReadWriteTimeout = 60000;
                http.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                var response = (HttpWebResponse)http.GetResponse();
                int status = (int)response.StatusCode;
                if (status < 300 || status >= 400) return response;
                string location = response.Headers[HttpResponseHeader.Location];
                response.Close();
                if (string.IsNullOrWhiteSpace(location)) throw new PackageException("The download server sent an invalid redirect.");
                var next = new Uri(uri, location);
                if (next.Scheme != Uri.UriSchemeHttps && uri.Scheme == Uri.UriSchemeHttps)
                    throw new PackageException("The download server redirected to an insecure address.");
                if (string.Equals(next.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) baseUri = next;
                uri = next;
            }
            throw new PackageException("The download server redirected too many times.");
        }

        static byte[] Download(Uri uri, out Uri responseUri)
        {
            using (var response = Open(uri, 20000, out responseUri))
            using (var stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[16384];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > 4 * 1024 * 1024) throw new PackageException("The download list is too large.");
                }
                return memory.ToArray();
            }
        }

        // ----- Install -------------------------------------------------------------------

        public delegate void ProgressHandler(long done, long total, string phase);

        public void Install(string gameId, PackageFeed feed, FeedPackage package, ProgressHandler progress)
        {
            string packagesDir = PackagesDirectory(gameId);
            string packageDir = Path.Combine(packagesDir, package.id);
            string downloads = Path.Combine(packagesDir, ".downloads");
            Directory.CreateDirectory(downloads);
            Directory.CreateDirectory(packageDir);
            string partial = Path.Combine(downloads, package.id + "-" + package.version + ".part");
            string staging = Path.Combine(packageDir, ".staging-" + package.version);
            string final = Path.Combine(packageDir, package.version);

            // After a rollback the newer version is still on disk: switch back to it without downloading.
            lock (RegistryLock)
            {
                var current = Resolve(gameId, package.id);
                if (current != null && current.previous == package.version && !string.IsNullOrEmpty(current.previous_sha256) &&
                    string.Equals(current.previous_sha256, package.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    progress(1, 1, "Installing");
                    RollBack(gameId, package.id);
                    return;
                }
            }

            try
            {
                DownloadVerified(new Uri(feed.BaseUri, package.url), partial, package, progress);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                progress(0, 1, "Installing");
                Extract(partial, staging);
                if (Directory.Exists(final)) Directory.Delete(final, true);
                Directory.Move(staging, final);
            }
            finally
            {
                TryDelete(partial);
                if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch { }
            }

            string previous = null, previousSha = null;
            lock (RegistryLock)
            {
                var registry = Installed(gameId);
                InstalledPackage old;
                if (registry.TryGetValue(package.id, out old))
                {
                    bool replaced = old.version != package.version;
                    previous = replaced ? old.version : old.previous;
                    previousSha = replaced ? old.sha256 : old.previous_sha256;
                }
                registry[package.id] = new InstalledPackage {
                    version = package.version, previous = previous, sha256 = package.sha256.ToLowerInvariant(),
                    previous_sha256 = previousSha, installed = DateTime.UtcNow.ToString("o")
                };
                WriteRegistry(gameId, registry);
            }

            // Keep the new version and the one before it (for rollback); remove anything older.
            foreach (var folder in Directory.GetDirectories(packageDir))
            {
                string name = Path.GetFileName(folder);
                if (name == package.version || name == previous) continue;
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        // Downloads and verifies a package from any feed (used for launcher updates) and extracts it into
        // an empty destination folder.
        public void DownloadAndExtract(PackageFeed feed, FeedPackage package, string destination, ProgressHandler progress)
        {
            string partial = Path.Combine(Path.GetTempPath(), "crosscrease-" + package.id + "-" + package.version + "-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                DownloadVerified(new Uri(feed.BaseUri, package.url), partial, package, progress);
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                progress(0, 1, "Installing");
                Extract(partial, destination);
            }
            finally { TryDelete(partial); }
        }

        void DownloadVerified(Uri uri, string target, FeedPackage package, ProgressHandler progress)
        {
            Uri ignored;
            using (var response = Open(uri, 30000, out ignored))
            using (var stream = response.GetResponseStream())
            using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sha = SHA256.Create())
            {
                long total = package.size > 0 ? package.size : response.ContentLength, done = 0;
                var buffer = new byte[81920];
                int read;
                var lastReport = DateTime.MinValue;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    file.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (package.size > 0 && done > package.size) throw new PackageException("The download is larger than expected.");
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 80) { progress(done, total, "Downloading"); lastReport = DateTime.UtcNow; }
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                progress(done, total, "Verifying");
                if (package.size > 0 && done != package.size) throw new PackageException("The download is incomplete. Please try again.");
                string hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(hash, package.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new PackageException("The downloaded file failed its integrity check and was discarded.");
            }
        }

        static void Extract(string zipFile, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.OpenRead(zipFile))
            {
                foreach (var entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.Length == 0) continue;
                    string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new PackageException("The package contains an unsafe path and was not installed.");
                    if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        // Removes downloaded packages only. Saves, settings and caches in the game's data folder stay.
        public void RemovePackages(string gameId)
        {
            string packagesDir = PackagesDirectory(gameId);
            if (Directory.Exists(packagesDir)) Directory.Delete(packagesDir, true);
        }

        void WriteRegistry(string gameId, Dictionary<string, InstalledPackage> registry)
        {
            string file = RegistryFile(gameId);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            File.WriteAllText(temp, serializer.Serialize(registry));
            if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        // ----- Versions ------------------------------------------------------------------

        // Semantic-version comparison; a prerelease sorts before its release.
        public static int CompareVersions(string a, string b)
        {
            string pa, pb;
            var na = ParseVersion(a, out pa);
            var nb = ParseVersion(b, out pb);
            for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
            {
                int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            if (pa.Length == 0 && pb.Length > 0) return 1;
            if (pa.Length > 0 && pb.Length == 0) return -1;
            return string.CompareOrdinal(pa, pb);
        }

        static int[] ParseVersion(string value, out string prerelease)
        {
            value = (value ?? "").Trim().TrimStart('v', 'V');
            int dash = value.IndexOf('-');
            prerelease = dash >= 0 ? value.Substring(dash + 1) : "";
            string core = dash >= 0 ? value.Substring(0, dash) : value;
            var parts = core.Split('.');
            var numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], out numbers[i]);
            return numbers;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024 * 1024) return Math.Max(1, bytes / 1024) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }
    }
}
