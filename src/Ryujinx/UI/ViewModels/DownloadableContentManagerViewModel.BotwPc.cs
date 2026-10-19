using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Common.Models;
using Ryujinx.HLE.BotwPc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.ViewModels
{
    /// <summary>BOTW-PC: per-package name, version, required game version and on-demand hash verification for BotW DLC.</summary>
    public partial class DownloadableContentManagerViewModel
    {
        [ObservableProperty] private string _botwDlcStatus = "";
        [ObservableProperty] private bool _botwDlcBusy;

        public bool IsBotw => _applicationData.IdBase == BotwProfile.TitleId;

        private uint InstalledGameVersion()
            => (uint)(_applicationLibrary.FindUpdateConfigurationFor(_applicationData.Id).FirstOrDefault(u => u.IsSelected).TitleUpdate?.Version ?? 0);

        public async void RefreshBotwDlcStatus() => await BuildStatus(verify: false);

        public async void VerifyBotwDlc() => await BuildStatus(verify: true);

        private async Task BuildStatus(bool verify)
        {
            if (!IsBotw || BotwDlcBusy)
            {
                return;
            }

            BotwDlcBusy = true;

            if (verify)
            {
                BotwDlcStatus = LocaleManager.Instance[LocaleKeys.BotwDlcVerifying];
            }

            List<(string Path, bool Enabled)> packages = DownloadableContents.Select(d => (d.ContainerPath, SelectedDownloadableContents.Contains(d))).Distinct().ToList();
            uint gameVersion = InstalledGameVersion();

            BotwDlcStatus = await Task.Run(() =>
            {
                StringBuilder text = new();
                HashSet<ulong> seen = [];

                foreach ((string path, bool enabled) in packages)
                {
                    foreach (BotwDlc.Package package in BotwDlc.Read(path, _applicationLibrary.VirtualFileSystem))
                    {
                        string duplicate = package.TitleId != 0 && !seen.Add(package.TitleId) ? " [duplicate: another package provides this add-on]" : "";
                        text.AppendLine($"{(enabled ? "✔" : "✘")} {package.Name} {BotwDlc.FormatVersion(package.Version)}: {BotwDlc.Compatibility(package, gameVersion)}{duplicate}");

                        if (verify && package.Error == null)
                        {
                            try
                            {
                                (int files, long bytes) = BotwDlc.Verify(path, package.NcaPath, _applicationLibrary.VirtualFileSystem);
                                text.AppendLine($"    {string.Format(LocaleManager.Instance[LocaleKeys.BotwDlcVerified], files, bytes / 1048576.0)}");
                            }
                            catch (Exception e) when (e is not OutOfMemoryException)
                            {
                                text.AppendLine($"    {LocaleManager.Instance[LocaleKeys.BotwDlcVerifyFailed]} {e.Message}");
                            }
                        }
                    }
                }

                return text.ToString().TrimEnd();
            });

            BotwDlcBusy = false;
        }
    }
}
