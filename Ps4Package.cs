using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CrossCrease
{
    // What the launcher learns about a PS4 .pkg before installing it.
    public sealed class Ps4PackageInfo
    {
        public string Path;
        public string ContentId = "";
        public string TitleId = "";
        public string Title = "";
        public string Category = "";
        public string AppVersion = "";
        public bool IsUpdate;
        public bool IsDeltaUpdate;
        public bool IsAddOn;
        public long PackageSize;
        public long InstallSize;
        public int FileCount;
        // A sentence for the player when the package cannot be installed, otherwise null.
        public string Problem;
    }

    public sealed class Ps4PackageException : Exception
    {
        public Ps4PackageException(string message) : base(message) { }
    }

    // Reads and unpacks PS4 game packages (.pkg) that were made for a PC or a homebrew-enabled console
    // ("fake" packages, fPKG). Those are protected with publicly known keys that open-source PS4 tools
    // such as LibOrbisPkg and earlier shadPS4 releases also use. Retail packages bought from a store are
    // encrypted for one console and cannot be unpacked here. No console or game keys are included.
    //
    // Layout: the .pkg holds a few system files (sce_sys) and an AES-XTS encrypted PFS image. That image
    // contains a compressed PFS image (PFSC, 64 KiB zlib blocks) whose directory tree is the game.
    public static class Ps4Package
    {
        const uint PkgMagic = 0x7F434E54;
        const int Sector = 0x1000;
        const int InodeSize = 0xA8;
        const int TypeFile = 2, TypeDirectory = 3;

        public static Ps4PackageInfo Inspect(string path)
        {
            var info = new Ps4PackageInfo { Path = path };
            try
            {
                using (var reader = new Reader(path))
                {
                    reader.ReadHeader();
                    info.PackageSize = reader.FileLength;
                    info.ContentId = reader.ContentId;
                    if (reader.ContentId.Length >= 16) info.TitleId = reader.ContentId.Substring(7, 9);
                    var sfo = reader.ParamSfo();
                    if (sfo != null)
                    {
                        string value;
                        if (sfo.TryGetValue("TITLE_ID", out value) && value.Length > 0) info.TitleId = value;
                        if (sfo.TryGetValue("TITLE", out value)) info.Title = value;
                        if (sfo.TryGetValue("CATEGORY", out value)) info.Category = value;
                        if (sfo.TryGetValue("APP_VER", out value)) info.AppVersion = value;
                    }
                    string category = info.Category.ToLowerInvariant();
                    info.IsUpdate = category == "gp" || (reader.ContentFlags & 0x40000000) != 0;
                    info.IsDeltaUpdate = info.IsUpdate && (reader.ContentFlags & 0x01000000) != 0;
                    info.IsAddOn = category == "ac" || category.StartsWith("a");
                    reader.ReadKeys();
                    reader.OpenImage();
                    var files = reader.ListFiles();
                    foreach (var file in files) { info.InstallSize += file.Size; info.FileCount++; }
                }
            }
            catch (Ps4PackageException ex) { info.Problem = ex.Message; }
            catch (IOException ex) { info.Problem = "The file could not be read: " + ex.Message; }
            catch (UnauthorizedAccessException ex) { info.Problem = "The file could not be opened: " + ex.Message; }
            catch (Exception ex) { info.Problem = "The package could not be read (" + ex.Message + ")."; }
            return info;
        }

        // Unpacks the package into destination (created if needed). progress receives (done, total) bytes.
        public static void Extract(string path, string destination, Action<long, long> progress, Func<bool> cancelled)
        {
            using (var reader = new Reader(path))
            {
                reader.ReadHeader();
                reader.ReadKeys();
                reader.OpenImage();
                var files = reader.ListFiles();
                long total = 0, done = 0;
                foreach (var file in files) total += file.Size;
                Directory.CreateDirectory(destination);
                reader.ExtractSystemFiles(Combine(destination, "sce_sys"));
                foreach (var folder in reader.Folders) Directory.CreateDirectory(Combine(destination, folder));
                foreach (var file in files)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    string target = Combine(destination, file.RelativePath);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    reader.ExtractFile(file, target, delegate(long written)
                    {
                        done += written;
                        if (progress != null) progress(done, total);
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    });
                }
                if (progress != null) progress(total, total);
            }
        }

        static string Combine(string root, string relative)
        {
            return System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        sealed class PkgEntry
        {
            public uint Id, NameOffset, Flags1, Flags2, Offset, Size;
            public byte[] Raw;
        }

        sealed class PfsFile
        {
            public string RelativePath;
            public long Size;
            public uint FirstBlock, Blocks;
        }

        sealed class Reader : IDisposable
        {
            readonly FileStream stream;
            readonly List<PkgEntry> entries = new List<PkgEntry>();
            public long FileLength;
            public string ContentId = "";
            public uint ContentFlags;
            long pfsImageOffset, pfsImageSize;
            byte[] dk3;
            ICryptoTransform dataDecryptor, tweakEncryptor;
            long pfscOffset, innerBlockSize;
            long[] blockMap;
            int innerBlocks;
            long inodeCount;
            readonly byte[] sectorTweaks = new byte[Sector];
            public readonly List<string> Folders = new List<string>();

            public Reader(string path)
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                FileLength = stream.Length;
            }

            public void Dispose()
            {
                if (dataDecryptor != null) dataDecryptor.Dispose();
                if (tweakEncryptor != null) tweakEncryptor.Dispose();
                stream.Dispose();
            }

            byte[] ReadAt(long offset, int count)
            {
                var buffer = new byte[count];
                if (offset < 0 || offset >= FileLength) return buffer;
                stream.Position = offset;
                int read = 0;
                while (read < count)
                {
                    int n = stream.Read(buffer, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }
                return buffer;
            }

            static uint Be32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
            static long Be64(byte[] b, int o) { return (long)((ulong)Be32(b, o) << 32 | Be32(b, o + 4)); }

            public void ReadHeader()
            {
                if (FileLength < 0x1000) throw new Ps4PackageException("This file is too small to be a PS4 game package.");
                var header = ReadAt(0, 0x1000);
                if (Be32(header, 0) != PkgMagic) throw new Ps4PackageException("This file is not a PS4 game package (.pkg).");
                uint entryCount = Be32(header, 0x10), tableOffset = Be32(header, 0x18);
                ContentId = Encoding.ASCII.GetString(header, 0x40, 0x24).TrimEnd('\0');
                ContentFlags = Be32(header, 0x78);
                pfsImageOffset = Be64(header, 0x410);
                pfsImageSize = Be64(header, 0x418);
                long declared = Be64(header, 0x430);
                if (declared > FileLength)
                    throw new Ps4PackageException("This package is incomplete (" + FormatGb(FileLength) + " of " + FormatGb(declared) + "). Copy or download it again.");
                if (entryCount == 0 || entryCount > 10000 || tableOffset + (long)entryCount * 32 > FileLength)
                    throw new Ps4PackageException("This package is damaged: its file table is invalid.");
                var table = ReadAt(tableOffset, (int)entryCount * 32);
                for (int i = 0; i < entryCount; i++)
                {
                    var raw = new byte[32];
                    Buffer.BlockCopy(table, i * 32, raw, 0, 32);
                    entries.Add(new PkgEntry {
                        Id = Be32(raw, 0), NameOffset = Be32(raw, 4), Flags1 = Be32(raw, 8), Flags2 = Be32(raw, 12),
                        Offset = Be32(raw, 16), Size = Be32(raw, 20), Raw = raw
                    });
                }
                if (pfsImageOffset <= 0 || pfsImageSize <= 0 || pfsImageOffset + pfsImageSize > FileLength)
                    throw new Ps4PackageException("This package does not contain game files CrossCrease can install.");
            }

            PkgEntry Entry(uint id)
            {
                foreach (var entry in entries) if (entry.Id == id) return entry;
                return null;
            }

            public Dictionary<string, string> ParamSfo()
            {
                var entry = Entry(0x1000);
                if (entry == null || entry.Size == 0 || entry.Size > 1024 * 1024 || (entry.Flags1 & 0x80000000) != 0) return null;
                return MainForm.ParseParamSfo(ReadAt(entry.Offset, (int)entry.Size));
            }

            public void ReadKeys()
            {
                var keys = Entry(0x10);
                var image = Entry(0x20);
                if (keys == null || image == null) throw new Ps4PackageException("This package is damaged: its keys are missing.");
                var encryptedDk3 = ReadAt(keys.Offset + 32 + 7 * 32 + 3 * 256, 256);
                try { dk3 = RsaDecrypt(Ps4PublicKeys.PkgDerivedKey3, encryptedDk3, 32); }
                catch (CryptographicException)
                {
                    throw new Ps4PackageException("This package is encrypted for a PlayStation 4 console and can't be unpacked on a PC. " +
                        "Use a package made from your own game for PC or homebrew use (often called a \"fake PKG\" or \"fPKG\").");
                }
                var ivKey = EntryKey(image);
                var imageKey = AesCbcDecrypt(ivKey, ReadAt(image.Offset, 256));
                byte[] ekpfs;
                try { ekpfs = RsaDecrypt(Ps4PublicKeys.Fake, imageKey, 32); }
                catch (CryptographicException) { throw new Ps4PackageException("This package uses protection CrossCrease can't unpack."); }

                var seed = ReadAt(pfsImageOffset + 0x370, 16);
                var message = new byte[20];
                message[0] = 1;
                Buffer.BlockCopy(seed, 0, message, 4, 16);
                byte[] mixed;
                using (var hmac = new HMACSHA256(ekpfs)) mixed = hmac.ComputeHash(message);
                var tweakKey = new byte[16];
                var dataKey = new byte[16];
                Buffer.BlockCopy(mixed, 0, tweakKey, 0, 16);
                Buffer.BlockCopy(mixed, 16, dataKey, 0, 16);
                using (var aes = new AesCryptoServiceProvider { Mode = CipherMode.ECB, Padding = PaddingMode.None })
                {
                    dataDecryptor = aes.CreateDecryptor(dataKey, null);
                    tweakEncryptor = aes.CreateEncryptor(tweakKey, null);
                }
            }

            byte[] EntryKey(PkgEntry entry)
            {
                var input = new byte[64];
                Buffer.BlockCopy(entry.Raw, 0, input, 0, 32);
                Buffer.BlockCopy(dk3, 0, input, 32, 32);
                using (var sha = SHA256.Create()) return sha.ComputeHash(input);
            }

            static byte[] AesCbcDecrypt(byte[] ivKey, byte[] data)
            {
                var key = new byte[16];
                var iv = new byte[16];
                Buffer.BlockCopy(ivKey, 0, iv, 0, 16);
                Buffer.BlockCopy(ivKey, 16, key, 0, 16);
                int length = data.Length / 16 * 16;
                var output = new byte[data.Length];
                using (var aes = new AesCryptoServiceProvider { Mode = CipherMode.CBC, Padding = PaddingMode.None })
                using (var decryptor = aes.CreateDecryptor(key, iv))
                    if (length > 0) decryptor.TransformBlock(data, 0, length, output, 0);
                return output;
            }

            static byte[] RsaDecrypt(RSAParameters parameters, byte[] data, int keep)
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(parameters);
                    var plain = rsa.Decrypt(data, false);
                    if (plain.Length < keep) throw new CryptographicException("Short key.");
                    var result = new byte[keep];
                    Buffer.BlockCopy(plain, 0, result, 0, keep);
                    return result;
                }
            }

            // Decrypts [offset, offset + count) of the outer PFS image (AES-XTS, 4 KiB sectors).
            byte[] ReadImage(long offset, int count)
            {
                long first = offset / Sector, last = (offset + count + Sector - 1) / Sector;
                int span = (int)((last - first) * Sector);
                var buffer = ReadAt(pfsImageOffset + first * Sector, span);
                var tweak = new byte[16];
                var start = new byte[16];
                for (int s = 0; s < span / Sector; s++)
                {
                    long sector = first + s;
                    Array.Clear(start, 0, 16);
                    for (int i = 0; i < 8; i++) start[i] = (byte)(sector >> (8 * i));
                    tweakEncryptor.TransformBlock(start, 0, 16, tweak, 0);
                    for (int b = 0; b < Sector; b += 16)
                    {
                        Buffer.BlockCopy(tweak, 0, sectorTweaks, b, 16);
                        int carry = 0;
                        for (int k = 0; k < 16; k++)
                        {
                            int next = (tweak[k] >> 7) & 1;
                            tweak[k] = (byte)((tweak[k] << 1) | carry);
                            carry = next;
                        }
                        if (carry != 0) tweak[0] ^= 0x87;
                    }
                    int at = s * Sector;
                    for (int i = 0; i < Sector; i++) buffer[at + i] ^= sectorTweaks[i];
                    dataDecryptor.TransformBlock(buffer, at, Sector, buffer, at);
                    for (int i = 0; i < Sector; i++) buffer[at + i] ^= sectorTweaks[i];
                }
                var result = new byte[count];
                Buffer.BlockCopy(buffer, (int)(offset - first * Sector), result, 0, count);
                return result;
            }

            public void OpenImage()
            {
                pfscOffset = -1;
                long limit = Math.Min(pfsImageSize, 256L * 1024 * 1024);
                for (long at = 0x20000; at + 0x30 < limit; at += 0x10000)
                {
                    var probe = ReadImage(at, 0x30);
                    if (BitConverter.ToUInt32(probe, 0) == 0x43534650) { pfscOffset = at; break; }
                }
                if (pfscOffset < 0) throw new Ps4PackageException("This package's game data could not be found. It may be damaged.");
                var header = ReadImage(pfscOffset, 0x30);
                innerBlockSize = BitConverter.ToInt64(header, 0x10);
                long mapOffset = BitConverter.ToInt64(header, 0x18);
                long dataLength = BitConverter.ToInt64(header, 0x28);
                if (innerBlockSize != 0x10000 || dataLength <= 0 || dataLength / innerBlockSize > int.MaxValue / 8 - 1)
                    throw new Ps4PackageException("This package uses a layout CrossCrease can't unpack yet.");
                innerBlocks = (int)(dataLength / innerBlockSize);
                var map = ReadImage(pfscOffset + mapOffset, (innerBlocks + 1) * 8);
                blockMap = new long[innerBlocks + 1];
                for (int i = 0; i <= innerBlocks; i++) blockMap[i] = BitConverter.ToInt64(map, i * 8);

                var super = InnerBlock(0);
                ushort mode = BitConverter.ToUInt16(super, 0x1C);
                if ((mode & 0x2) != 0) throw new Ps4PackageException("This package uses a 64-bit file system layout CrossCrease can't unpack yet.");
                if (BitConverter.ToInt32(super, 0x20) != innerBlockSize) throw new Ps4PackageException("This package uses a layout CrossCrease can't unpack yet.");
                inodeCount = BitConverter.ToInt64(super, 0x30);
                if (inodeCount <= 0 || inodeCount > 10000000) throw new Ps4PackageException("This package is damaged: its file list is invalid.");
            }

            byte[] InnerBlock(long index)
            {
                if (index < 0 || index >= innerBlocks) throw new Ps4PackageException("This package is damaged: a file points outside the game data.");
                long start = blockMap[index], size = blockMap[index + 1] - start;
                var block = new byte[innerBlockSize];
                if (size == 0) return block;
                if (size < 0 || size > innerBlockSize) throw new Ps4PackageException("This package is damaged: its game data is invalid.");
                var raw = ReadImage(pfscOffset + start, (int)size);
                if (size == innerBlockSize) return raw;
                if (size < 2) throw new Ps4PackageException("This package is damaged: its game data is invalid.");
                using (var input = new MemoryStream(raw, 2, raw.Length - 2))
                using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int read = 0;
                    while (read < block.Length)
                    {
                        int n = inflate.Read(block, read, block.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                return block;
            }

            struct Inode
            {
                public ushort Mode;
                public long Size;
                public uint Blocks, Location;
                public bool IsDirectory { get { return (Mode & 0x4000) != 0; } }
            }

            Inode ReadInode(long number)
            {
                if (number < 0 || number >= inodeCount) throw new Ps4PackageException("This package is damaged: its file list is invalid.");
                long perBlock = innerBlockSize / InodeSize;
                var block = InnerBlock(1 + number / perBlock);
                int at = (int)(number % perBlock) * InodeSize;
                return new Inode {
                    Mode = BitConverter.ToUInt16(block, at),
                    Size = BitConverter.ToInt64(block, at + 8),
                    Blocks = BitConverter.ToUInt32(block, at + 0x60),
                    Location = BitConverter.ToUInt32(block, at + 0x64)
                };
            }

            struct DirectoryEntry
            {
                public string Name;
                public long Inode;
                public int Type;
            }

            List<DirectoryEntry> ReadDirectory(Inode directory)
            {
                var result = new List<DirectoryEntry>();
                for (uint b = 0; b < directory.Blocks; b++)
                {
                    var block = InnerBlock(directory.Location + b);
                    int at = 0;
                    while (at + 16 <= block.Length)
                    {
                        int ino = BitConverter.ToInt32(block, at), type = BitConverter.ToInt32(block, at + 4);
                        int nameLength = BitConverter.ToInt32(block, at + 8), entrySize = BitConverter.ToInt32(block, at + 12);
                        if (ino == 0 || entrySize <= 0) break;
                        if (nameLength > 0 && nameLength <= 255 && at + 16 + nameLength <= block.Length && (type == TypeFile || type == TypeDirectory))
                            result.Add(new DirectoryEntry { Name = Encoding.UTF8.GetString(block, at + 16, nameLength), Inode = ino, Type = type });
                        at += entrySize;
                    }
                }
                return result;
            }

            public List<PfsFile> ListFiles()
            {
                var files = new List<PfsFile>();
                Folders.Clear();
                var super = InnerBlock(0);
                long superRoot = BitConverter.ToInt64(super, 0x48);
                long uroot = -1;
                foreach (var entry in ReadDirectory(ReadInode(superRoot)))
                    if (entry.Name == "uroot" && entry.Type == TypeDirectory) uroot = entry.Inode;
                if (uroot < 0) throw new Ps4PackageException("This package's game folder could not be found. It may be damaged.");
                var visited = new HashSet<long> { superRoot };
                Walk(uroot, "", files, visited, 0);
                return files;
            }

            void Walk(long directory, string prefix, List<PfsFile> files, HashSet<long> visited, int depth)
            {
                if (!visited.Add(directory) || depth > 64) throw new Ps4PackageException("This package is damaged: its folders loop.");
                var inode = ReadInode(directory);
                if (!inode.IsDirectory) throw new Ps4PackageException("This package is damaged: its file list is invalid.");
                foreach (var child in ReadDirectory(inode))
                {
                    string name = child.Name;
                    if (!SafeName(name)) throw new Ps4PackageException("This package contains a file name CrossCrease can't use: " + name);
                    string relative = prefix.Length == 0 ? name : prefix + "/" + name;
                    if (child.Type == TypeDirectory)
                    {
                        Folders.Add(relative);
                        Walk(child.Inode, relative, files, visited, depth + 1);
                    }
                    else
                    {
                        var file = ReadInode(child.Inode);
                        if (file.Size < 0 || (long)file.Blocks * innerBlockSize < file.Size)
                            throw new Ps4PackageException("This package is damaged: " + relative + " is invalid.");
                        files.Add(new PfsFile { RelativePath = relative, Size = file.Size, FirstBlock = file.Location, Blocks = file.Blocks });
                    }
                }
            }

            static bool SafeName(string name)
            {
                if (name.Length == 0 || name == "." || name == ".." || name.Trim().Length == 0) return false;
                return name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
            }

            public void ExtractFile(PfsFile file, string target, Action<long> written)
            {
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    long remaining = file.Size;
                    for (uint b = 0; b < file.Blocks && remaining > 0; b++)
                    {
                        var block = InnerBlock(file.FirstBlock + b);
                        int count = (int)Math.Min(remaining, block.Length);
                        output.Write(block, 0, count);
                        remaining -= count;
                        written(count);
                    }
                }
            }

            static readonly Dictionary<uint, string> SystemNames = new Dictionary<uint, string> {
                { 0x400, "license.dat" }, { 0x401, "license.info" }, { 0x402, "nptitle.dat" }, { 0x403, "npbind.dat" },
                { 0x404, "selfinfo.dat" }, { 0x406, "imageinfo.dat" }, { 0x407, "target-deltainfo.dat" },
                { 0x408, "origin-deltainfo.dat" }, { 0x409, "psreserved.dat" }, { 0x1000, "param.sfo" }
            };

            // Writes the package's sce_sys files (param.sfo, icons, npbind.dat, trophies, ...).
            public void ExtractSystemFiles(string folder)
            {
                var namesEntry = Entry(0x200);
                var names = namesEntry != null && namesEntry.Size > 0 && namesEntry.Size < 4 * 1024 * 1024 ? ReadAt(namesEntry.Offset, (int)namesEntry.Size) : new byte[0];
                foreach (var entry in entries)
                {
                    if (entry.Id < 0x400) continue;
                    string name = null;
                    if (entry.NameOffset != 0 && entry.NameOffset < names.Length)
                    {
                        int end = (int)entry.NameOffset;
                        while (end < names.Length && names[end] != 0) end++;
                        name = Encoding.UTF8.GetString(names, (int)entry.NameOffset, end - (int)entry.NameOffset);
                    }
                    if (string.IsNullOrEmpty(name) && !SystemNames.TryGetValue(entry.Id, out name)) continue;
                    var parts = name.Split('/');
                    bool safe = true;
                    foreach (var part in parts) if (!SafeName(part)) safe = false;
                    if (!safe) continue;
                    var data = ReadAt(entry.Offset, (int)entry.Size);
                    if ((entry.Flags1 & 0x80000000) != 0)
                    {
                        if (((entry.Flags2 & 0xF000) >> 12) != 3) continue;
                        var padded = new byte[(data.Length + 15) / 16 * 16];
                        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
                        var plain = AesCbcDecrypt(EntryKey(entry), padded);
                        data = new byte[entry.Size];
                        Buffer.BlockCopy(plain, 0, data, 0, data.Length);
                    }
                    string target = Combine(folder, name);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, data);
                }
            }
        }

        static string FormatGb(long bytes) { return (bytes / 1073741824.0).ToString("0.0") + " GB"; }
    }

    // Publicly known RSA keysets for PS4 "fake" packages. They are the same values that open-source PS4
    // tools publish (for example LibOrbisPkg and shadPS4 up to 0.6.0); they only open packages built
    // with them. Console keys and game keys are never part of CrossCrease.
    static class Ps4PublicKeys
    {
        public static readonly RSAParameters PkgDerivedKey3 = Make(
            "D212FC335F6DDB831609628B0356273782D477853529392D526B8C4C8CFB06C1845BE7D4F7BCD24E6245CD2ABBD77776453655273FB3F5F98EDA4BEFAA59AEB39BEA5498D206326A58312AE0D44F90B50A7DECF43A9C52672D99318E0C43E682FE0746E12E50D41F2D2F7ED908BA06B3BF2E203F4E3FFE44FFAA50435791699449158282E40F4C8D9D2CC95B1D64BF888BD4C594E76547841EE57910FB989347B97D8512A640982CF792BC951932EDE890560D65C1AA78C62E54FD5F54A1F67EE5E05F61C120B4B9B4330870E4DF8956ED012946775F8CB8A9F51E2EB3B9BFE009B78D28D4A6C3B81E1F07EBB4120B95B88530FDDC3913D07CDC8FEDF9C9A3C1",
            "32D903908FBDB08F572B285E0B8DB3EA5CD17EA890888CDD6A80BBB1DFC1F70DAA32F0B77CCB88800E8B64B0BE4CD60E9B8C1E2A64E1F35CD77601415E935C94FEDD4662C31B5AE2A0BC2DEBC3980AA7B7856970682B644AB31FCC7DDC7C26F477F65CF2AE5A442DD3AB16620419BAFB90FFE23050896ECB56B2EBC09116925E308EAEC7945DFD35E120F8AD3EBC08BFC036749FD5BB5208FD0666F37AB304F475295DE95FAA1030B20F5A1AC12AB3FECB21AD80EC8F20091CDBC55894C29CC6CE82653E5790BCA98B06B4F072F677DF9864F1ECFE372DBCAE8C08811FC3C9891AC742824B2EDC8E8D73CEB1CC01D90870873C4408EC498F815AE240FF77FC0D",
            "F967AD9912310C56A22E161C46B34D5B43BE42A2F686968042C3C73FC342F58749339F075D6E2C04FDE3E1B2AE0A0CF0C7A61CA16350C8099C5124526C5E5EBD1E2706BBBC9E94E135D46DB3CB3C68DD68B3FE6CCB8D8220762363B7E96810014EDCBA275D01C12D805E2BAF826BD884B6105286A7898EAE9AE289C6F7D587FB",
            "D7A10F9A8BF2C91195329A8CF0D94047F568A00DBDC1FC432F65F9C3610F257754ADD758AC8440608D3FF3658975B5C62C511A2F1F22E4431154BEC9B4C7B51B050BBC569ACD4AD973685E5CFB92B78B0DFFF507CAB4C89B963C079E3E6B2A11F28AB18AD72E1BA5532406ED50B89067B1E241C69201EE10F061BBFBB27D4A73",
            "52CC2DA09C9E75E728EE3DDEE345D14F941CCCC88729453B8D6EAB6E2AA7C71543A3048F905FEBF3384A77FA36B71576B6011A8E258782F155D8C6432AC0E598C932D1946FD901BA0681E06D88F2242A2501645CBFF2D999673EF672EEE4E2335CF80040E32A9AF43D2286443CFB0AA57C3FCCF5F116C4AC88B4DE6294926A13",
            "7C9DAD39E0D560149448197F8895D58B80AD858A4B773785D077BBBF89714A72CB726838EC02C67DC6440633511CC0FF958F0D75DC25BB0B7391A96D42D803B768D41E7562A37035797800C8F5EF15B9FC4E475AC870705B5298C0C2584A7096CCB810E12F788B2BA17FF9ACDEF0BB2BE266E3229231215792C4B8F23E762037",
            "459755D422085EF35CB4057AFDAA4242AD9A8CA06CBB1D6854546E3E32E3537376F13E01EAD3CFEBEB233EC0BECEEC2C895FA8273A4CB7E674BC454C26C825FF34632537E14810C193A6AFEBBAE3A2F13DEF63D8F4FDD3EEE25DE933CCADBA755C85AFCEA93DD1A217F3F698B3508E5EF6EB028EA162A7D62CEC91FF1540D2E3");

        public static readonly RSAParameters Fake = Make(
            "C6CF71E7E59AF0D12A2C458BF92A0EC143058BC37117801DCD497DDE359D259BA0D7A0F27D6C087EAA5502682B23C644B84418EB56CF16A24803C9E74F87EB3D30C31588BF20E79DFF770CDE1D241E63A94F8ABF5BBE601968333BFCED9F474E5FF8EACB3D00BD6701F92C6DC6AC1364E76714F3DC52696AB9832C4230131BB2D8A5020D79ED96B10DF8CC0CDF81954F035809570E80692EFEFF5277EA7528A8FBC9BEBF9FBBB7798E1805E180BD50349481D353C269A2D24CCF6CF4572C104A3FFB22FD8B97E2C95BA62BCDD61B6BDB687F4BC2A05034C005E58DEF2467FF9340CF2D62A2A050B1F13AA83DFD80D1F9B80522AFC8354590588EE33A7CBD3E27",
            "7F76CD0EE2D4DE051CC6D9A80E8DFA7BCA1EAA271A40F8F1228735DDDBFDEEF8C2BCBD01FB8BE23E63B2B1225C56496E11BE07440B9A2666D1492C8FD31BCFA4A1B8D1FBA49ED2212883098AF6A00BA3D60F9B6368CCBC0C4E145B27A4A9F42BB9B87BC0E651AD1D77D46BB9CE20D126667E5E9EA2E96B90F373B8528F4411030C1397393D132258D5438249DA6E7CA1C58CA5B009E0CE3DDFF49D3C9715E26AC72B3C509323DBBA4A226644AC78BB0E1A2743B57167AFF4AB48469373D042AB9363E56C9ADE5024C0237D99793F2207E0C148561BDF830912B42D456BC9C06885999079961AD7F54D1F3783404AEC3937A680927DC580C7D66FFE8A7989C6B1",
            "FEF6BF1D69AB16250847556B86E43588722AB13DF8B644CAB3AB19D10424280A7455B8154509CC131CF2BA37A903908F0210FF257986CC18509A105F5B4C1C4EB0A7E359B12DA0C6B0202C213312B3AF723483CD522FAF0F205A1BC0E2A376340FD7FCC141C9F979401742213E9DFDC7C150DE445AC931896A7805BE65B4E82D",
            "C79E4758007D6282B0D22281D4A8971B790C3AB0D7C930E3C3538E57EFF09B9FB39052C6942236AAE64A5F721D70E87658C8B291CE9CC3E9097F2E4797CC9039153531DE1F0C8C0DC1C292BE97BF2F91A18C7D50A8212FD7A29A7EB5A72A9002D9F33DD1EBB8E05A799E7D8DCA186DBD9EA180286B2AFE51249B6F4D84778023",
            "6D48E0544025C84129524227EBD2C7AB6B9C270AB41F944EFA421DB7BCB9AEBC046F758F105F89ACAB9CD2FAE6A4138368D45638FEE52B78449C34E65AA0BE0570AD15C32D31AC975D88FCC1623DE2ED11DBB69EFC5A5A03F6CF08D45D90C92AB99BCFC81A65F35BE87FCFA5A64C5C2A120F92A5E3F0171E9A974586FDDB5425",
            "2A51CE02442850E830207C9C55BF6039BCD1F0E768F8085B611FA7BFD0E88BB5B1D5D916AC750C6DF2E0B59775D268161F007D8B17E8784841712B18968011DB68399CD6E0724286F01B160D3E12943D25A8A9309E545AD6366CD68C20628FA16B1F7C6DB2B1C12EAD36029C3ACA2F09D2459EEBF2BC6CAA3B3E90BC3867354D",
            "0B671C0D6C57D3E7056594315655FD2808FA058ACC5539619763A016273DEDC116402A12EA6FD9D85856A8568B0D385E1E803B5F40806F624F28A269F3D3F7FDB2C3524320929D978DA01507156EA40D56D3371AC49EDF0249B80A8462F5FAB93FA40976CCAAB99BA64FC16A64CED877AB4BF9A0AEDAF167877C985C7EB873F5");

        static RSAParameters Make(string modulus, string d, string p, string q, string dp, string dq, string inverseQ)
        {
            return new RSAParameters {
                Modulus = Hex(modulus), Exponent = new byte[] { 1, 0, 1 }, D = Hex(d), P = Hex(p), Q = Hex(q),
                DP = Hex(dp), DQ = Hex(dq), InverseQ = Hex(inverseQ)
            };
        }

        static byte[] Hex(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
