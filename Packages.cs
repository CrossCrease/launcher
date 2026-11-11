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

        public static string UserAgent { get { return "CrossCrease-Launcher/" + BuildInfo.Version; } }

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

        // Folder of the installed package, or null when it is not installed (or its files are gone).
        public string InstalledPath(string gameId, string packageId)
        {
            InstalledPackage entry;
            if (!SafeName.IsMatch(packageId ?? "") || !Installed(gameId).TryGetValue(packageId, out entry) || !SafeName.IsMatch(entry.version ?? ""))
                return null;
            string path = Path.Combine(PackagesDirectory(gameId), packageId, entry.version);
            return Directory.Exists(path) ? path : null;
        }

        public string InstalledVersion(string gameId, string packageId)
        {
            InstalledPackage entry;
            return InstalledPath(gameId, packageId) != null && Installed(gameId).TryGetValue(packageId, out entry) ? entry.version : null;
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

        static byte[] Download(Uri uri, out Uri responseUri)
        {
            var request = WebRequest.Create(uri);
            request.Timeout = 20000;
            var http = request as HttpWebRequest;
            if (http != null)
            {
                http.UserAgent = UserAgent;
                http.AllowAutoRedirect = true;
                http.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            }
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                responseUri = response.ResponseUri;
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

            var registry = Installed(gameId);
            InstalledPackage old;
            string previous = registry.TryGetValue(package.id, out old) && old.version != package.version ? old.version : (old != null ? old.previous : null);
            registry[package.id] = new InstalledPackage {
                version = package.version, previous = previous, sha256 = package.sha256.ToLowerInvariant(),
                installed = DateTime.UtcNow.ToString("o")
            };
            WriteRegistry(gameId, registry);

            // Keep the new version and the one before it (for rollback); remove anything older.
            foreach (var folder in Directory.GetDirectories(packageDir))
            {
                string name = Path.GetFileName(folder);
                if (name == package.version || name == previous) continue;
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        void DownloadVerified(Uri uri, string target, FeedPackage package, ProgressHandler progress)
        {
            var request = WebRequest.Create(uri);
            request.Timeout = 30000;
            var http = request as HttpWebRequest;
            if (http != null) { http.UserAgent = UserAgent; http.AllowAutoRedirect = true; http.ReadWriteTimeout = 60000; }
            using (var response = request.GetResponse())
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
