using Avalonia.Controls;
using Avalonia.Interactivity;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Windows;

namespace Ryujinx.Ava.UI.Views.Settings
{
    public partial class SettingsBotwPcView : UserControl
    {
        public SettingsBotwPcView()
        {
            InitializeComponent();
        }

        private void ControllerTest_Click(object sender, RoutedEventArgs e)
        {
            new BotwControllerTestWindow().Show();
        }

        private void RebuildPrompts_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as SettingsViewModel)?.RebuildBotwPrompts();
        }

        private void ResetBindings_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as SettingsViewModel)?.ResetBotwBindings();
        }
    }
}
