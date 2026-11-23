using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace DiskDelta
{
    internal sealed class DuplicateSet
    {
        public string Folder;
        public string Digest;
        public CleanupFile[] Files;
        public long ExtraBytes { get { return Files[0].Info.Size * (Files.Length - 1); } }
    }

    internal static class Duplicates
    {
        public static List<DuplicateSet> Scan(string root, bool recursive, CancellationToken stop)
        {
            PrivateFiles.VerifyDirectory(root);
            var protectedRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            if (Protected(root, protectedRoots)) throw new IOException("请选择用户文件夹。");

            var results = new List<DuplicateSet>();
            var pending = new Stack<string>();
            pending.Push(root);
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[128 * 1024];
                while (pending.Count != 0)
                {
                    stop.ThrowIfCancellationRequested();
                    string folder = pending.Pop();
                    var candidates = new List<CleanupFile>();
                    foreach (string path in Cleanup.Entries(folder))
                    {
                        stop.ThrowIfCancellationRequested();
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (recursive && !Protected(path, protectedRoots)) pending.Push(path);
                            continue;
                        }
                        PrivateFiles.Info info;
                        if (PrivateFiles.TryCandidate(path, long.MaxValue, out info) && info.Size > 0)
                            candidates.Add(new CleanupFile { Root = folder, Path = path, Info = info });
                    }
                    foreach (var size in candidates.GroupBy(file => file.Info.Size).Where(group => group.Count() > 1))
                    {
                        var byContent = new Dictionary<string, List<CleanupFile>>(StringComparer.Ordinal);
                        foreach (CleanupFile file in size)
                        {
                            stop.ThrowIfCancellationRequested();
                            try
                            {
                                using (FileStream stream = PrivateFiles.OpenDuplicate(file.Path, file.Info, false))
                                {
                                    string digest = Hash(stream, hash, buffer, stop);
                                    List<CleanupFile> matches;
                                    if (!byContent.TryGetValue(digest, out matches))
                                        byContent.Add(digest, matches = new List<CleanupFile>());
                                    matches.Add(file);
                                }
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        foreach (var entry in byContent.Where(item => item.Value.Count > 1))
                            results.Add(new DuplicateSet { Folder = folder, Digest = entry.Key, Files = entry.Value.ToArray() });
                    }
                }
            }
            return results.OrderByDescending(set => set.ExtraBytes)
                .ThenBy(set => set.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static Cleanup.Result Apply(Dictionary<DuplicateSet, HashSet<CleanupFile>> selections)
        {
            var result = new Cleanup.Result();
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[128 * 1024];
                var copyBuffer = new byte[buffer.Length];
                foreach (var selection in selections)
                {
                    DuplicateSet set = selection.Key;
                    HashSet<CleanupFile> selected = selection.Value;
                    FileStream kept = null;
                    try
                    {
                        foreach (CleanupFile file in set.Files.Where(file => !selected.Contains(file)))
                        {
                            try
                            {
                                kept = PrivateFiles.OpenDuplicate(file.Path, file.Info, false);
                                if (Hash(kept, hash, buffer, CancellationToken.None) == set.Digest) break;
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                            if (kept != null) kept.Dispose();
                            kept = null;
                        }
                        foreach (CleanupFile file in selected)
                        {
                            if (kept != null && DeleteMatching(file, kept, buffer, copyBuffer))
                            {
                                result.Deleted++;
                                result.Bytes += file.Info.Size;
                            }
                            else result.Skipped++;
                        }
                    }
                    finally { if (kept != null) kept.Dispose(); }
                }
            }
            return result;
        }

        private static bool Protected(string path, string[] roots)
        {
            string volume = Path.GetPathRoot(path);
            return roots.Concat(new[] { Path.Combine(volume, "$Recycle.Bin"), Path.Combine(volume, "System Volume Information") })
                .Any(root => !string.IsNullOrEmpty(root) && (string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                    PrivateFiles.Below(path, root)));
        }

        private static string Hash(Stream stream, HashAlgorithm hash, byte[] buffer, CancellationToken stop)
        {
            hash.Initialize();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                stop.ThrowIfCancellationRequested();
                hash.TransformBlock(buffer, 0, read, null, 0);
            }
            hash.TransformFinalBlock(buffer, 0, 0);
            return Convert.ToBase64String(hash.Hash);
        }

        private static bool DeleteMatching(CleanupFile file, Stream kept, byte[] buffer, byte[] copyBuffer)
        {
            try
            {
                kept.Position = 0;
                using (FileStream copy = PrivateFiles.OpenDuplicate(file.Path, file.Info, true))
                {
                    int length;
                    while ((length = kept.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        int read = 0;
                        while (read < length)
                        {
                            int count = copy.Read(copyBuffer, read, length - read);
                            if (count == 0) return false;
                            read += count;
                        }
                        for (int index = 0; index < length; index++)
                            if (buffer[index] != copyBuffer[index]) return false;
                    }
                    return copy.ReadByte() < 0 && PrivateFiles.DeleteDuplicate(copy, file.Path);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
