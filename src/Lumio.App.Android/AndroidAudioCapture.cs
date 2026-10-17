using System;
using System.IO;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// Records 16-bit PCM audio into a WAV file for video messages. Prefers a USB audio input (UAC, e.g. the
/// Nikon Z 50 II microphone over USB) and falls back to the phone/tablet microphone.
/// </summary>
internal sealed class AndroidAudioCapture : IDisposable
{
    private const string LogTag = "LumioAudio";
    private const int SampleRate = 48000;

    private readonly AudioRecord _record;
    private readonly FileStream _wav;
    private readonly Thread _thread;
    private readonly int _channels;
    private volatile bool _running = true;
    private long _dataBytes;

    private AndroidAudioCapture(AudioRecord record, FileStream wav, int channels)
    {
        _record = record;
        _wav = wav;
        _channels = channels;
        WriteWavHeader(_wav, 0, channels);
        _record.StartRecording();
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "LumioAudioCapture" };
        _thread.Start();
    }

    public static IDisposable? Start(Activity activity, string wavPath)
    {
        try
        {
#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M &&
                activity.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            {
                Log.Warn(LogTag, "Microphone permission missing - video message without sound");
                return null;
            }
#pragma warning restore CA1416

            var usbInput = FindUsbInput(activity);
            var channels = usbInput?.GetChannelCounts()?.Contains(2) == true ? 2 : 1;
            var channelMask = channels == 2 ? ChannelIn.Stereo : ChannelIn.Mono;
            var minBuffer = AudioRecord.GetMinBufferSize(SampleRate, channelMask, Encoding.Pcm16bit);
            if (minBuffer <= 0)
                return null;

            var record = new AudioRecord(AudioSource.Camcorder, SampleRate, channelMask, Encoding.Pcm16bit, Math.Max(minBuffer * 4, 64 * 1024));
            if (record.State != State.Initialized)
            {
                record.Release();
                return null;
            }

            if (usbInput is not null)
            {
                record.SetPreferredDevice(usbInput);
                Log.Info(LogTag, $"Recording from USB audio: {usbInput.ProductName} ({channels} ch)");
            }
            else
            {
                Log.Info(LogTag, "Recording from built-in microphone");
            }

            var wav = new FileStream(wavPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            return new AndroidAudioCapture(record, wav, channels);
        }
        catch (Exception ex)
        {
            // Audio is optional: the video message is still recorded without sound
            Log.Warn(LogTag, $"Audio capture unavailable: {ex.Message}");
            return null;
        }
    }

    private static AudioDeviceInfo? FindUsbInput(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.M)
            return null;

        var audioManager = (AudioManager?)context.GetSystemService(Context.AudioService);
#pragma warning disable CA1416
        return audioManager?.GetDevices(GetDevicesTargets.Inputs)?
            .FirstOrDefault(d => d.Type is AudioDeviceType.UsbDevice or AudioDeviceType.UsbHeadset or AudioDeviceType.UsbAccessory);
#pragma warning restore CA1416
    }

    private void ReadLoop()
    {
        var buffer = new byte[SampleRate * _channels * 2 / 10]; // 100 ms
        while (_running)
        {
            var read = _record.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                _wav.Write(buffer, 0, read);
                _dataBytes += read;
            }
            else if (read < 0)
            {
                Log.Warn(LogTag, $"AudioRecord.Read error {read}");
                break;
            }
        }
    }

    private static void WriteWavHeader(System.IO.Stream stream, long dataBytes, int channels)
    {
        var byteRate = SampleRate * channels * 2;
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1); // PCM
        writer.Write((ushort)channels);
        writer.Write((uint)SampleRate);
        writer.Write((uint)byteRate);
        writer.Write((ushort)(channels * 2));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write((uint)dataBytes);
    }

    public void Dispose()
    {
        if (!_running)
            return;

        _running = false;
        try { _record.Stop(); } catch { }
        _thread.Join(2000);
        _record.Release();

        _wav.Seek(0, SeekOrigin.Begin);
        WriteWavHeader(_wav, _dataBytes, _channels);
        _wav.Dispose();
    }
}
