using HidSharp;

namespace BatSignal.Core.Hidpp;

public sealed class HidppException(string message, byte errorCode, bool isHidpp10) : Exception(message)
{
    public byte ErrorCode { get; } = errorCode;

    /// <summary>True for a HID++ 1.0 error (sub-id 0x8F), false for a HID++ 2.0 error (feature index 0xFF).</summary>
    public bool IsHidpp10 { get; } = isHidpp10;
}

/// <summary>
/// One HID++ endpoint (a receiver, or a device connected directly). On Windows the short (0x10)
/// and long (0x11) HID++ reports live in two separate HID collections, so we hold both streams
/// and listen on both: a reply or an error can arrive on either one.
/// </summary>
public sealed class HidppChannel : IDisposable
{
    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;
    public const int ShortLength = 7;
    public const int LongLength = 20;

    readonly HidStream? _short;
    readonly HidStream? _long;
    readonly object _requestLock = new();
    volatile Pending? _pending;
    volatile bool _disposed;

    public string Name { get; }
    public int ProductId { get; }
    public string Key { get; }

    /// <summary>Raised for every report that is not the reply to a pending request (device notifications).</summary>
    public event Action<HidppChannel, byte[]>? Notification;

    /// <summary>Raised for every report received or sent, for debugging.</summary>
    public event Action<string, byte[]>? Trace;

    internal HidppChannel(string key, string name, int productId, HidStream? shortStream, HidStream? longStream)
    {
        Key = key;
        Name = name;
        ProductId = productId;
        _short = shortStream;
        _long = longStream;
        StartReader(_short);
        StartReader(_long);
    }

    void StartReader(HidStream? stream)
    {
        if (stream is null) return;
        stream.ReadTimeout = Timeout.Infinite;
        new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = $"HID++ reader {Name}" }.Start();
    }

    void ReadLoop(HidStream stream)
    {
        var buffer = new byte[Math.Max(stream.Device.GetMaxInputReportLength(), LongLength)];
        while (!_disposed)
        {
            int n;
            try { n = stream.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            catch { break; } // stream closed or device unplugged

            if (n < 4 || (buffer[0] != ShortReportId && buffer[0] != LongReportId)) continue;
            var report = buffer[..n];
            Trace?.Invoke("<<", report);

            var pending = _pending;
            if (pending is not null && pending.Matches(report))
                pending.Reply.TrySetResult(report);
            else
                Notification?.Invoke(this, report);
        }
    }

    /// <summary>
    /// Sends one request and waits for its reply. <paramref name="b2"/>/<paramref name="b3"/> are the
    /// feature index and function|swId for HID++ 2.0, or the sub-id and register address for HID++ 1.0.
    /// Returns the full reply report (payload starts at index 4).
    /// </summary>
    public byte[] Request(byte deviceIndex, byte b2, byte b3, ReadOnlySpan<byte> payload, bool useLong, int timeoutMs = 1000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_requestLock)
        {
            var stream = (useLong ? _long : _short) ?? _long ?? _short
                         ?? throw new InvalidOperationException("No HID++ stream open.");
            bool isLong = stream == _long;
            var report = new byte[isLong ? LongLength : ShortLength];
            report[0] = isLong ? LongReportId : ShortReportId;
            report[1] = deviceIndex;
            report[2] = b2;
            report[3] = b3;
            payload[..Math.Min(payload.Length, report.Length - 4)].CopyTo(report.AsSpan(4));

            var pending = new Pending(deviceIndex, b2, b3);
            _pending = pending;
            try
            {
                Trace?.Invoke(">>", report);
                stream.Write(report);
                if (!pending.Reply.Task.Wait(timeoutMs))
                    throw new TimeoutException($"No reply from device 0x{deviceIndex:X2} on {Name}.");

                var r = pending.Reply.Task.Result;
                if (r[0] == LongReportId && r[2] == 0xFF && r[3] == b2 && r[4] == b3)
                    throw new HidppException($"HID++ 2.0 error 0x{r[5]:X2}", r[5], isHidpp10: false);
                if (r[2] == 0x8F && r[3] == b2 && r[4] == b3)
                    throw new HidppException($"HID++ 1.0 error 0x{r[5]:X2}", r[5], isHidpp10: true);
                return r;
            }
            finally
            {
                _pending = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _short?.Dispose();
        _long?.Dispose();
    }

    sealed class Pending(byte deviceIndex, byte b2, byte b3)
    {
        public readonly TaskCompletionSource<byte[]> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Matches(byte[] r) =>
            r.Length >= 5 && r[1] == deviceIndex &&
            ((r[2] == b2 && r[3] == b3) ||                    // normal reply
             (r[2] == 0xFF && r[3] == b2 && r[4] == b3) ||   // HID++ 2.0 error
             (r[2] == 0x8F && r[3] == b2 && r[4] == b3));    // HID++ 1.0 error
    }
}
