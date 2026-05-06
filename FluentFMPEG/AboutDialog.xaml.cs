using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace FluentFMPEG
{
    public sealed partial class AboutDialog : ContentDialog
    {
        public AboutDialog()
        {
            InitializeComponent();
            var v = Package.Current.Id.Version;
            VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
