using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.UI.ViewModels;
using System.Globalization;

namespace Ryujinx.Ava.UI.Models
{
    public partial class ModModel : BaseModel
    {
        [ObservableProperty] private bool _enabled;

        public bool InSd { get; }
        public string Path { get; }
        public string Name { get; }

        // BOTW-PC: category / dependency notes, and whether the row shows priority buttons.
        public string Details { get; init; } = "";
        public bool HasDetails => !string.IsNullOrEmpty(Details);
        public bool CanReorder { get; init; }

        public string FormattedName =>
            InSd && ulong.TryParse(Name, NumberStyles.HexNumber, null, out ulong applicationId)
                ? $"Atmosphère: {RyujinxApp.MainWindow.ApplicationLibrary.GetNameForApplicationId(applicationId)}"
                : Name;

        public ModModel(string path, string name, bool enabled, bool inSd)
        {
            Path = path;
            Name = name;
            Enabled = enabled;
            InSd = inSd;
        }
    }
}
