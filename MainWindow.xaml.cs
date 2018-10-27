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
            //AppVersionInfo = $"Current version: {System.Reflection.Assembly.GetEntryAssembly().GetName().Version}";
            bool restart = false;
            var appPath = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\SquirrelDemo\\SquirrelDemo.exe";
            using (var updateManager = new UpdateManager(@"C:\Users\SUMOHAP\SquirrelDemo"))
            {
                //AppVersionInfo = $"Current version: {updateManager.CurrentlyInstalledVersion()}";
                var appUpdateInfo = await updateManager.CheckForUpdate();
                if (appUpdateInfo != null && appUpdateInfo.ReleasesToApply.Any())
                {
                    var newVersion = appUpdateInfo.ReleasesToApply[appUpdateInfo.ReleasesToApply.Count - 1];
                    var msgBoxresult = MessageBox.Show($"Update Version: {newVersion.Version}. Press ok to update.", "New version detected", MessageBoxButton.YesNo);
                    if (msgBoxresult == MessageBoxResult.Yes)
                    {
                        var releaseEntry = await updateManager.UpdateApp();
                        MessageBox.Show($"App upgraded to: {releaseEntry?.Version.ToString() ?? "No update"}" +
                                        $"{Environment.NewLine}" +
                                        $"Path={appPath}", "New app info");
                        //updateManager.RemoveUninstallerRegistryEntry();
                        //await updateManager.CreateUninstallerRegistryEntry();
                        //Process.Start(newExePath);
                        //await Task.Run(()=>UpdateManager.RestartApp());
                        restart = true;
                    }
                }
            }
            if (restart)
            {
                Process.Start(appPath);
                Application.Current.Shutdown(0);
            }
            //UpdateManager.RestartApp();
        }

        public string AppVersionInfo => $"s/w version: {System.Reflection.Assembly.GetEntryAssembly().GetName().Version}";
        //public string AppVersionInfo { get; set; }

        public string AppLocation => $"{System.Reflection.Assembly.GetEntryAssembly().Location}";
    }
}
