using Splat;
using Squirrel;
using System;
using System.Linq;
using System.Threading;
using System.Windows;

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
            MessageBox.Show("Hello");
            using (var logger = new SetupLogLogger(false) {Level = LogLevel.Info})
            {
                try
                {
                    ReleaseEntry releaseEntry = null;
                    //var appPath =
                    //    $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\SquirrelDemo\\SquirrelDemo.exe";
                    var rootDirectory =
                        $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\";
                    var newVersion = default(string);
                    using (var updateManager = new UpdateManager(Constants.PackagePath,"SquirrelDemo",rootDirectory))
                    {
                        logger.Write($"Get package from url details", LogLevel.Info);
                        if (updateManager.IsInstalledApp)
                        {
                            logger.Write($"App is installed", LogLevel.Info);
                            var appUpdateInfo = await updateManager.CheckForUpdate();
                            newVersion = appUpdateInfo.FutureReleaseEntry.Version.ToString();
                            logger.Write($"Check for update done", LogLevel.Info);
                            if (appUpdateInfo != null && appUpdateInfo.ReleasesToApply.Any())
                            {
                                logger.Write($"Release version to apply: {newVersion}", LogLevel.Info);
                                string msg = $"New version available!" +
                                             $"\n\nCurrent version: {appUpdateInfo.CurrentlyInstalledVersion.Version}" +
                                             $"\nNew version: {newVersion}" +
                                             $"\n\nUpdate application now?";
                                var msgBoxresult = MessageBox.Show(msg, "New version detected", MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);
                                if (msgBoxresult == MessageBoxResult.Yes)
                                {
                                    this.Visibility = Visibility.Hidden;
                                    releaseEntry = await updateManager.UpdateApp();
                                    //Environment.Exit(0);
                                    //logger.Write($"App upgraded to version: {appUpdateInfo.FutureReleaseEntry.Version}", LogLevel.Info);
                                    //MessageBox.Show(
                                    //    $"App upgraded to version: {appUpdateInfo.FutureReleaseEntry.Version}.\nPress OK to launch new version." +
                                    //    $"{Environment.NewLine}", "New app info");
                                }
                            }
                        }
                    }

                    if (releaseEntry != null)
                    {
                        //this.Visibility = Visibility.Hidden;
                        logger.Write($"Restart app", LogLevel.Info);
                        var exeToStart =
                            $"{rootDirectory}SquirrelDemo\\app-{newVersion}\\SquirrelDemo.exe";
                        System.Diagnostics.Process.Start(exeToStart);
                        Thread.Sleep(1000);
                        Application.Current.Shutdown(0);

                        //UpdateManager.RestartApp(exeToStart);
                        //await UpdateManager.RestartAppWhenExited(exeToStart).ContinueWith(t =>
                        //{
                        //    if (t.IsCompleted)
                        //    {
                        //        Environment.Exit(0);
                        //    }
                        //});
                    }
                }
                catch (Exception ex)
                {
                    logger.Write($"Exception message: {ex?.Message}" +
                                 $"\nInner Exception message:{ex.InnerException?.Message}  ", LogLevel.Error);
                }
            }
        }

        public string AppVersionInfo => $"s/w version: {System.Reflection.Assembly.GetEntryAssembly().GetName().Version}";

        public string AppLocation => $"{System.Reflection.Assembly.GetEntryAssembly().Location}";
    }
}
