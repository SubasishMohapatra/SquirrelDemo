using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Squirrel;

namespace SquirrelDemo
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            //MessageBox.Show("Hello");
            ReleaseEntry releaseEntry = null;
            var appPath = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\SquirrelDemo\\SquirrelDemo.exe";

            using (var updateManager = new UpdateManager(Constants.PackagePath))
            {
                if (updateManager.IsInstalledApp)
                {
                    var appUpdateInfo = await updateManager.CheckForUpdate();
                    if (appUpdateInfo != null && appUpdateInfo.ReleasesToApply.Any())
                    {
                        var newVersion = appUpdateInfo.ReleasesToApply.OrderBy(x => x.Version).Last();
                        string msg = $"New version available!" +
                                     $"\n\nCurrent version: {appUpdateInfo.CurrentlyInstalledVersion.Version}" +
                                     $"\nNew version: {appUpdateInfo.FutureReleaseEntry.Version}" +
                                     $"\n\nUpdate application now?";
                        var msgBoxresult = MessageBox.Show(msg, "New version detected", MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                        if (msgBoxresult == MessageBoxResult.Yes)
                        {
                            releaseEntry = await updateManager.UpdateApp();
                            MessageBox.Show($"App upgraded to: {releaseEntry?.Version.ToString() ?? "No update"}" +
                                            $"{Environment.NewLine}" +
                                            $"Path={appPath}", "New app info");
                        }
                    }
                }
            }
            if (releaseEntry!=null)
            {
                this.Visibility = Visibility.Hidden;
                UpdateManager.RestartApp();
            }
        }

        public string AppVersionInfo => $"s/w version: {System.Reflection.Assembly.GetEntryAssembly().GetName().Version}";

        public string AppLocation => $"{System.Reflection.Assembly.GetEntryAssembly().Location}";
    }
}
