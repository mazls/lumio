using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Lumio.App.Avalonia;
using Lumio.Camera;
using Lumio.Core.Camera;
using Lumio.Imaging;

namespace Lumio.App.Android;

[Activity(
    Label = "Lumio",
    Icon = "@mipmap/icon",
    RoundIcon = "@mipmap/icon",
    Theme = "@style/Theme.Lumio.NoActionBar",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout)]
// Plugging in a supported camera opens Lumio; with "always open" ticked, Android grants USB access without asking again
[IntentFilter([UsbManager.ActionUsbDeviceAttached])]
[MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
public class MainActivity : AvaloniaMainActivity<Lumio.App.Avalonia.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            // EGL rendering deadlocks the UI thread inside a synchronous compositor render on some GPUs
            // (e.g. Pixel 10 / PowerVR): dispatcher posts and timers never run, so the UI stops responding.
            // Vulkan only renders a black screen there, so use software rendering.
            .With(new AndroidPlatformOptions { RenderingMode = [AndroidRenderingMode.Software] });
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Detach MainView if it was previously attached to avoid InvalidOperationException on activity recreation
        DetachMainViewFromVisualParent();

        // Camera hooks must be registered BEFORE base.OnCreate: that attaches the Avalonia view, whose MainView
        // immediately starts the camera. Otherwise the desktop WebcamCamera is picked and hangs on Android.

        // Register custom camera discovery on Android (USB-OTG Nikon PTP, USB webcams via UVC & tablet lenses)
        CameraManager.CustomCameraDiscovery = () =>
        {
            var list = new List<CameraDeviceInfo>();

            // 1. USB-OTG Nikon PTP devices (D5600, etc.)
            list.AddRange(AndroidNikonPtpCamera.FindConnectedDevices(this));

            // 2. USB webcams via our own UVC driver (Nikon Z 50 II in UVC/UAC mode)
            list.AddRange(AndroidUvcCamera.FindConnectedDevices(this));

            // 3. Camera2 devices (Front selfie, Back, external cameras if the phone exposes them)
            list.AddRange(AndroidCameraService.FindConnectedDevices(this));

            return Task.FromResult<IReadOnlyList<CameraDeviceInfo>>(list);
        };

        // Register Android camera driver factory into CameraManager
        CameraManager.CustomCameraFactory = settings =>
        {
            if (settings.CameraBackend == Lumio.Core.Settings.CameraBackend.Simulated)
            {
                return new SimulatedCamera();
            }

            var deviceId = settings.WebcamDeviceId;
            var uvcConnected = AndroidUvcCamera.IsDeviceConnected(this);
            var ptpConnected = AndroidNikonPtpCamera.IsDeviceConnected(this);
            global::Android.Util.Log.Info("Lumio", $"Camera factory: backend={settings.CameraBackend}, device={deviceId}, uvc={uvcConnected}, ptp={ptpConnected}");

            // An explicitly selected USB camera wins while it is plugged in
            if (deviceId?.StartsWith("uvc:", System.StringComparison.OrdinalIgnoreCase) == true && uvcConnected)
            {
                return new AndroidUvcCamera(this, settings);
            }
            if (deviceId?.StartsWith("ptp:", System.StringComparison.OrdinalIgnoreCase) == true && ptpConnected)
            {
                return new AndroidNikonPtpCamera(this, settings);
            }

            // Otherwise a plugged-in external camera takes priority over the built-in phone/tablet cameras
            if (settings.CameraBackend == Lumio.Core.Settings.CameraBackend.Dslr && ptpConnected)
            {
                return new AndroidNikonPtpCamera(this, settings);
            }
            if (uvcConnected)
            {
                return new AndroidUvcCamera(this, settings);
            }
            if (ptpConnected)
            {
                return new AndroidNikonPtpCamera(this, settings);
            }

            return new AndroidCameraService(this, settings);
        };

        // Printing: Android has no printer drivers - find the Selphy (AirPrint/IPP) in the WLAN and print to it directly
        AndroidNetworkPrinters.Start(this);
        Lumio.Printing.SelphyPrintService.CustomPrinterDiscovery = AndroidNetworkPrinters.GetPrinters;
        Lumio.Printing.SelphyPrintService.CustomPrintHandler = AndroidNetworkPrinters.PrintAsync;

        // Video messages: record sound from the camera's USB audio (UAC) or the phone microphone and encode MP4
        VideoRecorder.CustomAudioCapture = wavPath => AndroidAudioCapture.Start(this, wavPath);
        VideoRecorder.CustomMp4Encoder = AndroidMp4Encoder.EncodeAsync;

        base.OnCreate(savedInstanceState);

        // Keep screen on during photobooth kiosk operation
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        ApplyImmersiveMode();
        RequestRuntimePermissions();

        _dispatcherPump = new Handler(Looper.MainLooper!);
        _dispatcherPump.Post(PumpDispatcher);

        // Listen for USB device attach/detach to automatically detect Nikon cameras
        _usbReceiver = new UsbBroadcastReceiver();
        var filter = new global::Android.Content.IntentFilter();
        filter.AddAction(UsbManager.ActionUsbDeviceAttached);
        filter.AddAction(UsbManager.ActionUsbDeviceDetached);
#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            RegisterReceiver(_usbReceiver, filter, global::Android.Content.ReceiverFlags.Exported);
        }
        else
        {
            RegisterReceiver(_usbReceiver, filter);
        }
#pragma warning restore CA1416

        // Listen for Camera2 external UVC cameras (e.g. Nikon Z 50 II in USB streaming mode)
        try
        {
            var cm = (global::Android.Hardware.Camera2.CameraManager?)GetSystemService(global::Android.Content.Context.CameraService);
            if (cm is not null)
            {
                _cameraCallback = new AndroidCameraAvailabilityCallback();
                cm.RegisterAvailabilityCallback(_cameraCallback, null);
            }
        }
        catch { }
    }

    // Camera and microphone (video messages) are asked for up front, so no dialog interrupts a guest later
    private void RequestRuntimePermissions()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.M)
            return;

#pragma warning disable CA1416
        var missing = new[] { global::Android.Manifest.Permission.Camera, global::Android.Manifest.Permission.RecordAudio }
            .Where(p => CheckSelfPermission(p) != Permission.Granted)
            .ToArray();
        if (missing.Length > 0)
            RequestPermissions(missing, 100);
#pragma warning restore CA1416
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) ApplyImmersiveMode();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyImmersiveMode();
    }

    // Fullscreen immersive kiosk mode: draw edge-to-edge (incl. display cutout) and hide system bars.
    // Must be re-applied on focus/resume, since the system restores the bars after dialogs, swipes or rotation.
    private void ApplyImmersiveMode()
    {
        var window = Window;
        if (window is null) return;

#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P && window.Attributes is { } attrs)
        {
            attrs.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            window.Attributes = attrs;
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            window.SetDecorFitsSystemWindows(false);
            var controller = window.InsetsController;
            if (controller is not null)
            {
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                controller.Hide(WindowInsets.Type.SystemBars());
            }
        }
        else
#pragma warning restore CA1416
        {
#pragma warning disable CS0618
            if (window.DecorView is not null)
            {
                window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    SystemUiFlags.ImmersiveSticky |
                    SystemUiFlags.Fullscreen |
                    SystemUiFlags.HideNavigation |
                    SystemUiFlags.LayoutStable |
                    SystemUiFlags.LayoutFullscreen |
                    SystemUiFlags.LayoutHideNavigation);
            }
#pragma warning restore CS0618
        }
    }

    private Handler? _dispatcherPump;

    // Avalonia's Android dispatcher loses wake-up signals that arrive while the UI thread is inside a
    // compositor render (dispatcher processing suspended). Posts from background threads (camera
    // callbacks, async continuations, DispatcherTimers) then never run and the UI looks frozen.
    // Periodically drain the dispatcher queue on the main looper as a safety net.
    private void PumpDispatcher()
    {
        try
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        catch (System.InvalidOperationException)
        {
            // Processing currently suspended (we are nested inside a render) - retry on the next tick.
        }
        _dispatcherPump?.PostDelayed(PumpDispatcher, 16);
    }

    private UsbBroadcastReceiver? _usbReceiver;
    private AndroidCameraAvailabilityCallback? _cameraCallback;

    private class UsbBroadcastReceiver : global::Android.Content.BroadcastReceiver
    {
        public override void OnReceive(global::Android.Content.Context? context, global::Android.Content.Intent? intent)
        {
            var action = intent?.Action;
            if (action == UsbManager.ActionUsbDeviceAttached || action == UsbManager.ActionUsbDeviceDetached)
            {
                global::Android.Util.Log.Info("Lumio", $"USB device changed: {action}");
                Lumio.Camera.CameraManager.NotifyHardwareChanged(devicesChanged: true);
            }
        }
    }

    private class AndroidCameraAvailabilityCallback : global::Android.Hardware.Camera2.CameraManager.AvailabilityCallback
    {
        private readonly long _startTime = System.Environment.TickCount64;

        public override void OnCameraAvailable(string cameraId)
        {
            if (System.Environment.TickCount64 - _startTime < 4000) return;
            global::Android.Util.Log.Info("Lumio", $"Camera available: {cameraId}");
            Lumio.Camera.CameraManager.NotifyHardwareChanged();
        }

        public override void OnCameraUnavailable(string cameraId)
        {
            if (System.Environment.TickCount64 - _startTime < 4000) return;
            global::Android.Util.Log.Info("Lumio", $"Camera unavailable: {cameraId}");
            Lumio.Camera.CameraManager.NotifyHardwareChanged();
        }
    }

    protected override void OnDestroy()
    {
        _dispatcherPump?.RemoveCallbacksAndMessages(null);
        _dispatcherPump = null;

        if (_usbReceiver is not null)
        {
            try { UnregisterReceiver(_usbReceiver); } catch { }
            _usbReceiver = null;
        }
        if (_cameraCallback is not null)
        {
            try
            {
                var cm = (global::Android.Hardware.Camera2.CameraManager?)GetSystemService(global::Android.Content.Context.CameraService);
                cm?.UnregisterAvailabilityCallback(_cameraCallback);
            }
            catch { }
            _cameraCallback = null;
        }

        DetachMainViewFromVisualParent();
        base.OnDestroy();
    }

    private static void DetachMainViewFromVisualParent()
    {
        try
        {
            if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView &&
                singleView.MainView is global::Avalonia.Controls.Control control)
            {
                if (control.Parent is global::Avalonia.Controls.ContentControl parentContentControl)
                {
                    parentContentControl.Content = null;
                }
                else if (control.Parent is global::Avalonia.Controls.Presenters.ContentPresenter parentPresenter)
                {
                    parentPresenter.Content = null;
                }

                if (global::Avalonia.VisualTree.VisualExtensions.GetVisualParent(control) is global::Avalonia.Controls.Presenters.ContentPresenter visualPresenter)
                {
                    visualPresenter.Content = null;
                }
            }
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("Lumio", $"DetachMainView: {ex.Message}");
        }
    }
}
