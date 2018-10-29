using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Squirrel;

namespace SquirrelDemo
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            var updateManager = new UpdateManager(Constants.PackagePath);
            SquirrelAwareApp.HandleEvents(
                onInitialInstall: v =>
                {
                    updateManager.CreateShortcutForThisExe();
                    updateManager.CreateUninstallerRegistryEntry();
                },
                onAppUpdate: v =>
                {
                    //updateManager.RemoveShortcutForThisExe();
                    updateManager.CreateShortcutForThisExe();
                    //updateManager.RemoveUninstallerRegistryEntry();
                    updateManager.CreateUninstallerRegistryEntry();
                    //UpdateManager.RestartApp();
                },
                onAppUninstall: v =>
                {
                    updateManager.RemoveShortcutForThisExe();
                    updateManager.RemoveUninstallerRegistryEntry();
                });
            //onFirstRun: () => ShowTheWelcomeWizard = true);
            // Create the startup window
            MainWindow wnd = new MainWindow();
            // Do stuff here, e.g. to the window
            wnd.Title = "Squirrel Demo";
            // Show the window
            wnd.Show();
        }
    }
}
