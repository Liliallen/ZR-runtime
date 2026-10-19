using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.HLE.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using MissingKeyException = LibHac.Common.Keys.MissingKeyException;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Reads and verifies BotW add-on content packages with the emulator's own NSP/NCA/key handling.
    /// Installation itself stays Ryujinx's (games/&lt;id&gt;/dlc.json references the user's package; nothing is copied or modified).
    /// </summary>
    public static class BotwDlc
    {
        public static readonly IReadOnlyDictionary<ulong, string> Names = new Dictionary<ulong, string>
        {
            [0x01007EF00011F001] = "DLC Pack 1 – The Master Trials",
            [0x01007EF00011F002] = "DLC Pack 2 – The Champions' Ballad",
        };

        public sealed record Package(ulong TitleId, string Name, uint Version, uint RequiredGameVersion, string NcaPath, string Error)
        {
            public bool IsBotw => (TitleId & ~0xFFFUL) == (BotwProfile.TitleId | 0x1000);
        }

        /// <summary>Raw title version as shown by Ryujinx and the release number (version &gt;&gt; 16) used by Nintendo's update system.</summary>
        /// <summary>Add-on content title IDs registered for the running game (the overlay's DLC status).</summary>
        public static IList<ulong> EnabledInRunningGame(Switch device) => device?.System?.ContentManager?.GetAocTitleIds() ?? [];

        public static string FormatVersion(uint version) => $"v{version} (#{version >> 16})";

        /// <summary>Reads the add-on content metadata (cnmt) of a package: title, version and minimum game version.</summary>
        public static List<Package> Read(string packagePath, VirtualFileSystem vfs)
        {
            List<Package> result = [];

            try
            {
                using IFileSystem pfs = PartitionFileSystemUtils.OpenApplicationFileSystem(packagePath, vfs);

                foreach ((_, ContentMetaData meta) in pfs.GetContentData(ContentMetaType.AddOnContent, vfs, IntegrityCheckLevel.ErrorOnInvalid))
                {
                    string ncaPath = null;

                    foreach (DirectoryEntryEx entry in pfs.EnumerateEntries("/", "*.nca"))
                    {
                        Nca nca = pfs.GetNca(vfs.KeySet, entry.FullPath);

                        if (nca.Header.ContentType == NcaContentType.PublicData && nca.Header.TitleId == meta.Id)
                        {
                            ncaPath = entry.FullPath;
                        }
                    }

                    result.Add(new Package(meta.Id, Names.TryGetValue(meta.Id, out string name) ? name : $"Add-on {meta.Id:X16}",
                        meta.Version.Version, meta.RequiredApplicationVersion.Version, ncaPath,
                        ncaPath == null ? "the package has no data NCA for this add-on content (incomplete package)" : null));
                }

                if (result.Count == 0)
                {
                    result.Add(new Package(0, "", 0, 0, null, "no add-on content metadata found (not a DLC package)"));
                }
            }
            catch (Exception e) when (e is MissingKeyException or InvalidDataException or IOException or HorizonResultException or LibHacException)
            {
                result.Add(new Package(0, "", 0, 0, null, e is MissingKeyException mk ? $"missing key '{mk.Name}' (check prod.keys / title key)" : e.Message));
            }

            return result;
        }

        /// <summary>
        /// Reads every file of the add-on content data with hash verification (IntegrityCheckLevel.ErrorOnInvalid), so a
        /// corrupted or wrongly decrypted package fails. Returns (files, bytes) read, or throws on the first bad block.
        /// </summary>
        public static (int Files, long Bytes) Verify(string packagePath, string ncaPath, VirtualFileSystem vfs)
        {
            using IFileSystem pfs = PartitionFileSystemUtils.OpenApplicationFileSystem(packagePath, vfs);
            vfs.ImportTickets(pfs);

            Nca nca = pfs.GetNca(vfs.KeySet, ncaPath);
            IFileSystem romfs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
            byte[] buffer = new byte[1 << 20];
            int files = 0;
            long bytes = 0;

            foreach (DirectoryEntryEx entry in romfs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories))
            {
                if (entry.Type != DirectoryEntryType.File)
                {
                    continue;
                }

                using UniqueRef<IFile> file = new();
                romfs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                for (long offset = 0; offset < entry.Size; offset += buffer.Length)
                {
                    file.Get.Read(out long read, offset, buffer, ReadOption.None).ThrowIfFailure();
                    bytes += read;
                }

                files++;
            }

            return (files, bytes);
        }

        /// <summary>Title version of the update package (0 if none or unreadable).</summary>
        public static uint ReadGameVersion(string updatePath, VirtualFileSystem vfs)
        {
            if (string.IsNullOrEmpty(updatePath) || !File.Exists(updatePath))
            {
                return 0;
            }

            using IFileSystem pfs = PartitionFileSystemUtils.OpenApplicationFileSystem(updatePath, vfs);

            foreach ((_, ContentMetaData meta) in pfs.GetContentData(ContentMetaType.Patch, vfs, IntegrityCheckLevel.ErrorOnInvalid))
            {
                return meta.Version.Version;
            }

            return 0;
        }

        /// <summary>Compatibility of a package with the installed game version (title version of the active update).</summary>
        public static string Compatibility(Package package, uint installedGameVersion)
        {
            if (package.Error != null)
            {
                return package.Error;
            }

            if (!package.IsBotw)
            {
                return $"title {package.TitleId:X16} is not a Breath of the Wild add-on";
            }

            return installedGameVersion >= package.RequiredGameVersion
                ? $"compatible (needs game {FormatVersion(package.RequiredGameVersion)}, installed {FormatVersion(installedGameVersion)})"
                : $"needs game update {FormatVersion(package.RequiredGameVersion)}, installed {FormatVersion(installedGameVersion)}";
        }
    }
}
