using System;
using System.IO;
using System.IO.Compression;

namespace Ryujinx.HLE.BotwPc.Mods
{
    /// <summary>
    /// Copies a mod (folder or .zip) into a staging directory, rejecting anything that could escape it or exhaust
    /// the disk: absolute or ".." paths, symbolic links, too many/too large files, and extreme compression ratios.
    /// </summary>
    public static class ModArchive
    {
        public sealed record Limits(long MaxTotalBytes, long MaxFileBytes, int MaxFiles, long MaxRatio)
        {
            public static readonly Limits Default = new(8L << 30, 2L << 30, 200_000, 200);
        }

        public sealed class ImportException(string message) : Exception(message);

        /// <summary>Imports <paramref name="source"/> (directory or .zip) into the empty directory <paramref name="staging"/>.</summary>
        public static void Import(string source, string staging, Limits limits = null)
        {
            limits ??= Limits.Default;
            Directory.CreateDirectory(staging);

            if (Directory.Exists(source))
            {
                CopyDirectory(source, staging, limits);
            }
            else if (File.Exists(source))
            {
                string ext = Path.GetExtension(source).ToLowerInvariant();

                switch (ext)
                {
                    case ".zip":
                        ExtractZip(source, staging, limits);
                        break;
                    case ".bnp":
                        throw new ImportException("This is a BCML package (.bnp). BCML packages must be merged by BCML or UKMM first; import the merged Switch export folder instead.");
                    case ".7z":
                    case ".rar":
                        throw new ImportException($"{ext} archives are not supported. Extract it first and import the folder, or use a .zip.");
                    default:
                        throw new ImportException($"Unsupported file type '{ext}'. Import a folder or a .zip.");
                }
            }
            else
            {
                throw new ImportException($"'{source}' does not exist.");
            }
        }

        /// <summary>Returns the full destination path for an archive entry name, or throws if it is unsafe.</summary>
        public static string SafeDestination(string root, string entryName)
        {
            string name = entryName.Replace('\\', '/');

            if (name.Length == 0 || name.Contains('\0') || name.StartsWith('/') || (name.Length > 1 && name[1] == ':'))
            {
                throw new ImportException($"Unsafe path in archive: '{entryName}'.");
            }

            foreach (string part in name.Split('/'))
            {
                if (part == "..")
                {
                    throw new ImportException($"Unsafe path in archive: '{entryName}'.");
                }
            }

            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, name));

            if (!full.StartsWith(fullRoot, StringComparison.Ordinal))
            {
                throw new ImportException($"Unsafe path in archive: '{entryName}'.");
            }

            return full;
        }

        private static void ExtractZip(string zipPath, string staging, Limits limits)
        {
            using ZipArchive zip = ZipFile.OpenRead(zipPath);

            long total = 0;
            int files = 0;

            // Validate everything before writing anything.
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                SafeDestination(staging, entry.FullName);

                int unixType = (entry.ExternalAttributes >> 16) & 0xF000;

                if (unixType == 0xA000)
                {
                    throw new ImportException($"Archive contains a symbolic link: '{entry.FullName}'.");
                }

                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                files++;
                total += entry.Length;

                if (entry.Length > limits.MaxFileBytes || total > limits.MaxTotalBytes || files > limits.MaxFiles)
                {
                    throw new ImportException("Archive is too large to import safely.");
                }

                if (entry.Length > (1 << 20) && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > limits.MaxRatio)
                {
                    throw new ImportException($"Archive entry '{entry.FullName}' has a suspicious compression ratio.");
                }
            }

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string dest = SafeDestination(staging, entry.FullName);

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest));

                using Stream input = entry.Open();
                using FileStream output = new(dest, FileMode.CreateNew, FileAccess.Write);

                // Never trust the declared size: stop at the declared length + 1 byte.
                CopyLimited(input, output, entry.Length);
            }
        }

        private static void CopyLimited(Stream input, Stream output, long declared)
        {
            byte[] buffer = new byte[1 << 16];
            long written = 0;
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;

                if (written > declared)
                {
                    throw new ImportException("Archive entry is larger than declared.");
                }

                output.Write(buffer, 0, read);
            }
        }

        private static void CopyDirectory(string source, string staging, Limits limits)
        {
            long total = 0;
            int files = 0;
            string root = Path.GetFullPath(source);

            if (new DirectoryInfo(root).LinkTarget != null)
            {
                throw new ImportException("The selected folder is a symbolic link.");
            }

            // Validate first, then copy.
            foreach (FileSystemInfo info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ImportException($"Mod folder contains a symbolic link: '{Path.GetRelativePath(root, info.FullName)}'.");
                }

                if (info is FileInfo file)
                {
                    files++;
                    total += file.Length;

                    if (file.Length > limits.MaxFileBytes || total > limits.MaxTotalBytes || files > limits.MaxFiles)
                    {
                        throw new ImportException("Mod folder is too large to import safely.");
                    }
                }
            }

            foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string dest = SafeDestination(staging, Path.GetRelativePath(root, file.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                file.CopyTo(dest);
            }
        }
    }
}
