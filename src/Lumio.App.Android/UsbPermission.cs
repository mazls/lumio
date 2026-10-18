using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;

namespace Lumio.App.Android;

/// <summary>
/// Requests runtime access to a USB device (USB-OTG cameras) and waits for the user's answer.
/// </summary>
internal static class UsbPermission
{
    private const string ActionUsbPermission = "com.lumio.booth.USB_PERMISSION";

    public static async Task<bool> RequestAsync(Activity activity, UsbManager usbManager, UsbDevice device, CancellationToken cancellationToken = default)
    {
        if (usbManager.HasPermission(device))
            return true;

        global::Android.Util.Log.Info("LumioUsb", $"Requesting USB permission for {device.ProductName} ({device.DeviceName})");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new PermissionReceiver(device.DeviceName, tcs);
        var filter = new IntentFilter(ActionUsbPermission);

#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            activity.RegisterReceiver(receiver, filter, ReceiverFlags.Exported);
        else
            activity.RegisterReceiver(receiver, filter);
#pragma warning restore CA1416

        try
        {
            // Android 12+ needs a MUTABLE PendingIntent so the OS can add EXTRA_PERMISSION_GRANTED.
            // Android 14+ only allows mutable PendingIntents with an explicit (package-bound) Intent.
            var flags = PendingIntentFlags.UpdateCurrent;
#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                flags |= PendingIntentFlags.Mutable;
#pragma warning restore CA1416

            var intent = new Intent(ActionUsbPermission).SetPackage(activity.PackageName);
            var permissionIntent = PendingIntent.GetBroadcast(activity, device.DeviceId, intent, flags);
            usbManager.RequestPermission(device, permissionIntent);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var granted = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            global::Android.Util.Log.Info("LumioUsb", $"USB permission for {device.ProductName}: {granted}");
            return granted;
        }
        catch (System.OperationCanceledException)
        {
            global::Android.Util.Log.Warn("LumioUsb", $"No answer to USB permission request for {device.ProductName}");
            return usbManager.HasPermission(device);
        }
        finally
        {
            try { activity.UnregisterReceiver(receiver); } catch { }
        }
    }

    private sealed class PermissionReceiver : BroadcastReceiver
    {
        private readonly string? _deviceName;
        private readonly TaskCompletionSource<bool> _tcs;

        public PermissionReceiver(string? deviceName, TaskCompletionSource<bool> tcs)
        {
            _deviceName = deviceName;
            _tcs = tcs;
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != ActionUsbPermission)
                return;

#pragma warning disable CA1422
            var device = intent.GetParcelableExtra(UsbManager.ExtraDevice) as UsbDevice;
#pragma warning restore CA1422
            if (device is not null && _deviceName is not null && device.DeviceName != _deviceName)
                return;

            _tcs.TrySetResult(intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false));
        }
    }
}
