using Avalonia.Collections;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.BotwPc;
using Ryujinx.HLE.BotwPc.Mods;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.ViewModels
{
    /// <summary>BOTW-PC additions to the mod manager, active only for Breath of the Wild.</summary>
    public partial class ModManagerViewModel
    {
        private ModStore _store;

        [ObservableProperty] private string _conflictSummary = "";
        [ObservableProperty] private string _selectedProfile = "Custom";
        [ObservableProperty] private AvaloniaList<string> _backups = [];
        [ObservableProperty] private string _selectedBackup;

        public bool IsBotw => _applicationId == BotwProfile.TitleId;
        public string[] Profiles => ModStore.BuiltInProfiles;

        private List<ModStore.Conflict> _conflicts = [];

        public bool SafeMode
        {
            get => BotwPcSettings.Instance.Mods.SafeMode;
            set
            {
                BotwPcSettings settings = BotwPcSettings.Instance;
                settings.Mods.SafeMode = value;
                settings.Save();
                OnPropertyChanged();
            }
        }

        private static bool GameRunning => RyujinxApp.MainWindow?.ViewModel?.IsGameRunning ?? false;

        private void LoadBotwMods()
        {
            _store ??= ModStore.ForCurrentUser();

            HashSet<ulong> enabledDlc = RyujinxApp.MainWindow.ApplicationLibrary.DownloadableContents.Items
                .Where(d => d.IsEnabled && d.Dlc.TitleIdBase == (BotwProfile.TitleId & ~0x1FFFUL))
                .Select(d => d.Dlc.TitleId)
                .ToHashSet();

            foreach (ModStore.InstalledMod mod in _store.List())
            {
                string path = mod.Parts.TryGetValue(ModInspector.GameTitleId, out string game) ? game : mod.Parts.Values.First();
                List<string> notes = [];

                if (mod.Manifest != null)
                {
                    notes.Add(mod.Manifest.Category);
                }

                foreach (ulong dlc in mod.Parts.Keys.Where(t => t != ModInspector.GameTitleId && !enabledDlc.Contains(t)))
                {
                    notes.Add(string.Format(LocaleManager.Instance[LocaleKeys.BotwModNeedsDlc], $"{dlc & 0xF}"));
                }

                Mods.Add(new ModModel(path, mod.Name, mod.Enabled, false) { Details = string.Join(" · ", notes), CanReorder = true });
            }

            RefreshConflicts();
            Backups = [.. _store.BackupNames()];
        }

        private void RefreshConflicts()
        {
            _conflicts = _store.Conflicts();
            ConflictSummary = _conflicts.Count == 0
                ? LocaleManager.Instance[LocaleKeys.BotwModNoConflicts]
                : string.Format(LocaleManager.Instance[LocaleKeys.BotwModConflicts], _conflicts.Count, _conflicts.Count(c => c.NeedsMerge));
        }

        private void SaveBotw()
        {
            _store.Save(Mods.Select(m => (m.Name, SelectedMods.Contains(m))).ToList());
        }

        /// <summary>Saves the current selection, then moves a mod in the priority order.</summary>
        public void Move(ModModel model, int delta)
        {
            SaveBotw();
            _store.Move(model.Name, delta);
            LoadMods(_applicationId, _installedDlcIds);
        }

        public async void InstallZip()
        {
            IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocaleManager.Instance[LocaleKeys.BotwModInstallFile],
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Mod archive") { Patterns = ["*.zip", "*.bnp", "*.7z", "*.rar"] }],
            });

            foreach (IStorageFile file in files)
            {
                await InstallFrom(file.Path.LocalPath);
            }
        }

        public async void InstallFolder()
        {
            IReadOnlyList<IStorageFolder> folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocaleManager.Instance[LocaleKeys.BotwModInstallFolder],
                AllowMultiple = true,
            });

            foreach (IStorageFolder folder in folders)
            {
                await InstallFrom(folder.Path.LocalPath);
            }
        }

        private async Task InstallFrom(string path)
        {
            if (GameRunning)
            {
                await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.BotwModGameRunning]);
                return;
            }

            // Keep the user's current, unsaved selection.
            SaveBotw();

            string name = ModStore.SanitizeName(Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\')));
            bool replace = false;

            if (_store.List().Any(m => m.Name == name))
            {
                replace = await ContentDialogHelper.CreateChoiceDialog(
                    LocaleManager.Instance[LocaleKeys.BotwModInstallFile],
                    string.Format(LocaleManager.Instance[LocaleKeys.BotwModUpdatePrompt], name), "");

                if (!replace)
                {
                    return;
                }
            }

            ModStore.InstallResult result;

            try
            {
                result = await Task.Run(() => _store.Install(path, name, replace));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result = new ModStore.InstallResult { Name = name, Errors = [e.Message] };
            }

            Logger.Info?.Print(LogClass.Application, $"BOTW-PC mod install '{name}': {(result.Success ? "ok" : "failed")} {string.Join(" | ", result.Errors.Concat(result.Warnings))}");

            if (!result.Success)
            {
                await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.BotwModInstallFailed], string.Join("\n", result.Errors.Concat(result.Warnings)));
            }
            else if (result.Warnings.Count > 0)
            {
                await ContentDialogHelper.CreateWarningDialog(string.Format(LocaleManager.Instance[LocaleKeys.BotwModInstalledWithWarnings], result.Name), string.Join("\n", result.Warnings));
            }

            LoadMods(_applicationId, _installedDlcIds);
        }

        /// <summary>Removes a mod into the backup area (restorable). Refused while a game runs: RomFS files are read lazily.</summary>
        private void UninstallBotw(ModModel model)
        {
            if (GameRunning)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.BotwModGameRunning]));
                return;
            }

            SaveBotw();
            _store.Uninstall(model.Name);
            Logger.Info?.Print(LogClass.Application, $"BOTW-PC mod '{model.Name}' removed to backup");
        }

        public async void RestoreBackup()
        {
            if (string.IsNullOrEmpty(SelectedBackup))
            {
                return;
            }

            if (GameRunning)
            {
                await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.BotwModGameRunning]);
                return;
            }

            SaveBotw();
            _store.Rollback(SelectedBackup);
            LoadMods(_applicationId, _installedDlcIds);
        }

        public void ApplyProfile()
        {
            SaveBotw();
            _store.ApplyProfile(SelectedProfile);
            BotwPcSettings settings = BotwPcSettings.Instance;
            settings.Mods.Profile = SelectedProfile;
            settings.Save();
            LoadMods(_applicationId, _installedDlcIds);
        }

        public void SaveCustomProfile()
        {
            SaveBotw();
            _store.SaveProfile("Custom");
        }

        public async void ShowConflicts()
        {
            string text = string.Join("\n", _conflicts.Take(60).Select(c => $"{c.File}: {string.Join(" > ", c.Mods)}{(c.NeedsMerge ? "  [merge]" : "")}"));

            if (_conflicts.Count > 60)
            {
                text += $"\n… +{_conflicts.Count - 60}";
            }

            await ContentDialogHelper.CreateWarningDialog(LocaleManager.Instance[LocaleKeys.BotwModConflictsTitle], text);
        }
    }
}
