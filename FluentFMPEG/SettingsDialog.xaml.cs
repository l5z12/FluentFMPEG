using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFMPEG
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        public event EventHandler? ResetRequested;

        public SettingsDialog()
        {
            InitializeComponent();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
            Hide();
        }
    }
}
