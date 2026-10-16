using System.IO;
using System.Windows;
using Lumio.Camera;
using Lumio.Core.Session;
using Lumio.Core.Settings;
using Lumio.Core.Storage;

namespace Lumio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var msg = args.ExceptionObject.ToString();
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
            MessageBox.Show(msg, "Unbehandelter Fehler");
        };

        try
        {
            var timeProvider = TimeProvider.System;
            var settingsStore = new SettingsStore();
            var settingsService = new SettingsService(settingsStore);
            var storage = new EventStorage(settingsService, timeProvider);
            var cameraManager = new CameraManager();
            var workflow = new PhotoCaptureWorkflow(storage, timeProvider);
            var templateStore = new Lumio.Imaging.TemplateStore();
            var printService = new Lumio.Printing.SelphyPrintService();

            var mainWindow = new MainWindow(settingsService, storage, cameraManager, workflow, templateStore, printService);
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            var msg = ex.ToString();
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log"), msg);
            MessageBox.Show(msg, "Startfehler");
        }
    }
}
