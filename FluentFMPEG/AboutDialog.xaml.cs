using System.Reflection;
using Microsoft.UI.Xaml.Controls;

namespace FluentFMPEG
{
    public sealed partial class AboutDialog : ContentDialog
    {
        public AboutDialog()
        {
            InitializeComponent();
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = v is null ? "" : $"Version {v.ToString(3)}";
        }
    }
}
