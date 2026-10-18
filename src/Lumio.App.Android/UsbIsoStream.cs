using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lumio.App.Android;

/// <summary>
/// Isochronous USB IN transfers via Linux usbfs (the Android USB host Java API only offers bulk/control).
/// Most USB webcams (e.g. Anker PowerConf C200) stream video isochronously. Keeps several URBs queued so no
/// microframe is lost, hands every received packet to a callback and resubmits the URB.
/// Struct layout and ioctl numbers are for 64-bit Linux (arm64 / x64, the app's only ABIs).
/// </summary>
internal sealed class UsbIsoStream : IDisposable
{
    // usbdevfs ioctls: _IOR('U', 10, struct usbdevfs_urb), _IOW('U', 13, void*), _IO('U', 11)
    private const ulong IoctlSubmitUrb = 0x8038550A;
    private const ulong IoctlReapUrbNoDelay = 0x4008550D;
    private const ulong IoctlDiscardUrb = 0x550B;

    // struct usbdevfs_urb (64-bit): 56 byte header followed by 12 byte iso packet descriptors
    private const int UrbHeaderSize = 56;
    private const int PacketDescriptorSize = 12;
    private const byte UrbTypeIso = 0;
    private const int UrbFlagIsoAsap = 0x02;
    private const int EINTR = 4;
    private const int EAGAIN = 11;

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, IntPtr argument);

    private readonly int _fd;
    private readonly byte _endpoint;
    private readonly int _packetSize;
    private readonly int _packetsPerUrb;
    private readonly IntPtr[] _urbs;
    private readonly IntPtr[] _buffers;
    private readonly bool[] _inFlight;
    private readonly IntPtr _reapResult = Marshal.AllocHGlobal(IntPtr.Size);
    private bool _disposed;

    /// <param name="packetSize">Bytes per service interval (incl. high-bandwidth / SuperSpeed burst multipliers).</param>
    public UsbIsoStream(int fileDescriptor, int endpointAddress, int packetSize, int urbCount = 8)
    {
        _fd = fileDescriptor;
        _endpoint = (byte)endpointAddress;
        _packetSize = packetSize;
        // 32 packets per URB like the Linux uvcvideo driver, fewer for huge SuperSpeed packets (usbfs memory limit)
        _packetsPerUrb = Math.Clamp(256 * 1024 / Math.Max(1, packetSize), 8, 32);
        _urbs = new IntPtr[urbCount];
        _buffers = new IntPtr[urbCount];
        _inFlight = new bool[urbCount];

        for (var i = 0; i < urbCount; i++)
        {
            _urbs[i] = Marshal.AllocHGlobal(UrbHeaderSize + _packetsPerUrb * PacketDescriptorSize);
            _buffers[i] = Marshal.AllocHGlobal(_packetsPerUrb * packetSize);
        }
    }

    public void Start()
    {
        for (var i = 0; i < _urbs.Length; i++)
            Submit(i);
    }

    /// <summary>
    /// Reaps finished URBs until cancelled; returns false if the device is gone. <paramref name="onPacketLost"/>
    /// reports packets the host controller could not receive (the current frame is incomplete then).
    /// </summary>
    public bool Run(Action<byte[], int> onPacket, Action onPacketLost, CancellationToken token)
    {
        var packet = new byte[_packetSize];
        while (!token.IsCancellationRequested)
        {
            if (ioctl(_fd, IoctlReapUrbNoDelay, _reapResult) < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno is EAGAIN or EINTR)
                {
                    Thread.Sleep(1);
                    continue;
                }
                return false; // ENODEV etc.: camera unplugged
            }

            var urb = Marshal.ReadIntPtr(_reapResult);
            var index = (int)Marshal.ReadIntPtr(urb, 48); // usercontext
            _inFlight[index] = false;

            for (var p = 0; p < _packetsPerUrb; p++)
            {
                var descriptor = UrbHeaderSize + p * PacketDescriptorSize;
                var actual = Marshal.ReadInt32(urb, descriptor + 4);
                var status = Marshal.ReadInt32(urb, descriptor + 8);
                if (status != 0)
                {
                    onPacketLost();
                    continue;
                }
                if (actual <= 0)
                    continue;

                Marshal.Copy(_buffers[index] + p * _packetSize, packet, 0, Math.Min(actual, _packetSize));
                onPacket(packet, actual);
            }

            if (!token.IsCancellationRequested)
                Submit(index);
        }
        return true;
    }

    private void Submit(int index)
    {
        var urb = _urbs[index];
        for (var offset = 0; offset < UrbHeaderSize; offset += 4)
            Marshal.WriteInt32(urb, offset, 0);

        Marshal.WriteByte(urb, 0, UrbTypeIso);
        Marshal.WriteByte(urb, 1, _endpoint);
        Marshal.WriteInt32(urb, 8, UrbFlagIsoAsap);
        Marshal.WriteIntPtr(urb, 16, _buffers[index]);
        Marshal.WriteInt32(urb, 24, _packetsPerUrb * _packetSize);
        Marshal.WriteInt32(urb, 36, _packetsPerUrb); // number_of_packets
        Marshal.WriteIntPtr(urb, 48, (IntPtr)index); // usercontext
        for (var p = 0; p < _packetsPerUrb; p++)
        {
            var descriptor = UrbHeaderSize + p * PacketDescriptorSize;
            Marshal.WriteInt32(urb, descriptor, _packetSize);
            Marshal.WriteInt32(urb, descriptor + 4, 0);
            Marshal.WriteInt32(urb, descriptor + 8, 0);
        }

        if (ioctl(_fd, IoctlSubmitUrb, urb) < 0)
            throw new InvalidOperationException($"USB-Übertragung konnte nicht gestartet werden (errno {Marshal.GetLastPInvokeError()}).");
        _inFlight[index] = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Cancel outstanding URBs and collect them before the memory is freed
        for (var i = 0; i < _urbs.Length; i++)
        {
            if (_inFlight[i])
                ioctl(_fd, IoctlDiscardUrb, _urbs[i]);
        }

        var deadline = Environment.TickCount64 + 500;
        while (Array.Exists(_inFlight, f => f) && Environment.TickCount64 < deadline)
        {
            if (ioctl(_fd, IoctlReapUrbNoDelay, _reapResult) < 0)
            {
                if (Marshal.GetLastPInvokeError() != EAGAIN) break;
                Thread.Sleep(2);
                continue;
            }
            var index = (int)Marshal.ReadIntPtr(Marshal.ReadIntPtr(_reapResult), 48);
            if (index >= 0 && index < _inFlight.Length) _inFlight[index] = false;
        }

        // Only free buffers the kernel no longer uses (a stuck URB leaks rather than corrupts memory)
        for (var i = 0; i < _urbs.Length; i++)
        {
            if (_inFlight[i]) continue;
            Marshal.FreeHGlobal(_urbs[i]);
            Marshal.FreeHGlobal(_buffers[i]);
        }
        Marshal.FreeHGlobal(_reapResult);
    }
}
