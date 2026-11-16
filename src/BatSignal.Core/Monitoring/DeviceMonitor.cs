using BatSignal.Core.Hidpp;

namespace BatSignal.Core.Monitoring;

/// <summary>
/// Background poller: finds receivers, reads every paired device's battery, keeps the last
/// reading of devices that went to sleep, and refreshes early when a device reports a battery
/// change or (re)connects. All HID I/O happens on its own thread.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    readonly object _lock = new();
    readonly Dictionary<string, EndpointState> _endpoints = new();
    readonly Dictionary<string, DeviceSnapshot> _devices = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _disposed;
    long _lastEventRefreshTicks;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Raised on the monitor thread after every scan.</summary>
    public event Action<IReadOnlyList<DeviceSnapshot>>? Updated;
    public event Action<string>? Log;

    public DeviceMonitor(IEnumerable<DeviceSnapshot>? cached = null)
    {
        foreach (var d in cached ?? [])
            _devices[d.Id] = d with { Online = false };
        _thread = new Thread(Run) { IsBackground = true, Name = "DeviceMonitor" };
    }

    public void Start() => _thread.Start();

    public void RefreshNow() => _wake.Set();

    public IReadOnlyList<DeviceSnapshot> Snapshot()
    {
        lock (_lock)
            return _devices.Values
                .OrderBy(d => d.Kind switch { DeviceKind.Mouse => 0, DeviceKind.Keyboard => 1, DeviceKind.Gamepad => 2, _ => 3 })
                .ThenBy(d => d.Name)
                .ToList();
    }

    void Run()
    {
        while (!_disposed)
        {
            try { Scan(); }
            catch (Exception e) { Log?.Invoke($"Scan failed: {e}"); }

            if (_disposed) break;
            try { Updated?.Invoke(Snapshot()); }
            catch (Exception e) { Log?.Invoke($"Update handler failed: {e}"); }

            _wake.WaitOne(PollInterval);
        }
    }

    void Scan()
    {
        var found = HidppDiscovery.Find();
        var foundKeys = found.Select(e => e.Key).ToHashSet();

        foreach (var key in _endpoints.Keys.Where(k => !foundKeys.Contains(k)).ToList())
            CloseEndpoint(key);

        foreach (var ep in found.Where(e => !_endpoints.ContainsKey(e.Key)))
        {
            var ch = HidppDiscovery.Open(ep);
            if (ch is null)
            {
                Log?.Invoke($"Could not open {ep.Name} (0x{ep.ProductId:X4})");
                continue;
            }
            var state = new EndpointState(ch, ep);
            ch.Notification += (_, r) => OnNotification(state, r);
            _endpoints[ep.Key] = state;
            Log?.Invoke($"Opened {ep.Name} (0x{ep.ProductId:X4})");
        }

        var seen = new HashSet<string>();
        foreach (var state in _endpoints.Values.ToList())
        {
            try { ScanEndpoint(state, seen); }
            catch (Exception e)
            {
                Log?.Invoke($"Endpoint {state.Channel.Name} failed, closing: {e.Message}");
                CloseEndpoint(state.Channel.Key);
            }
        }

        lock (_lock)
            foreach (var id in _devices.Keys.Where(id => !seen.Contains(id)).ToList())
                _devices[id] = _devices[id] with { Online = false };
    }

    void ScanEndpoint(EndpointState state, HashSet<string> seen)
    {
        var ch = state.Channel;
        state.IsDirect ??= new HidppDevice(ch, 0xFF).Ping() is { Major: >= 2 };
        byte[] indexes = state.IsDirect == true ? [0xFF] : [1, 2, 3, 4, 5, 6];
        var receiver = new HidppReceiver(ch);

        foreach (var idx in indexes)
        {
            string id = $"{ch.ProductId:X4}/{(idx == 0xFF ? "direct" : idx.ToString())}";
            if (!state.Devices.TryGetValue(idx, out var dev))
                state.Devices[idx] = dev = new HidppDevice(ch, idx);

            var version = dev.Ping();
            if (version is null || version.Major < 2)
            {
                if (state.Identity.ContainsKey(idx))
                    Log?.Invoke($"{id}: no reply to ping, marking asleep");
                // Offline or empty slot. Forget cached features in case a different device pairs here.
                state.Devices.Remove(idx);
                state.Identity.Remove(idx);
                state.BatteryFeature.TryRemove(idx, out _);
                if (idx != 0xFF) AddFromPairingIfUnknown(receiver, idx, id, state);
                continue;
            }

            try
            {
                if (!state.Identity.TryGetValue(idx, out var ident))
                {
                    ident = (dev.GetName() ?? $"Device {idx}", dev.GetKind());
                    state.Identity[idx] = ident;
                }
                var battery = dev.ReadBattery();
                if (dev.BatteryFeatureIndex is byte bi) state.BatteryFeature[idx] = bi;

                lock (_lock)
                {
                    _devices.TryGetValue(id, out var previous);
                    _devices[id] = new DeviceSnapshot
                    {
                        Id = id,
                        Name = ident.Name,
                        Kind = ident.Kind,
                        Connection = state.Connection,
                        Battery = battery ?? previous?.Battery,
                        Online = true,
                        LastSeen = DateTimeOffset.Now,
                    };
                }
                seen.Add(id);
            }
            catch (Exception e) when (e is HidppException or TimeoutException or NotSupportedException)
            {
                Log?.Invoke($"{id}: {e.Message}"); // went to sleep mid-read; keep the last reading
            }
        }
    }

    void AddFromPairingIfUnknown(HidppReceiver receiver, byte slot, string id, EndpointState state)
    {
        lock (_lock)
            if (_devices.ContainsKey(id)) return;

        var p = receiver.GetPairing(slot);
        if (p?.Name is null) return;
        lock (_lock)
            _devices[id] = new DeviceSnapshot { Id = id, Name = p.Name, Kind = p.Kind, Connection = state.Connection };
    }

    void OnNotification(EndpointState state, byte[] r)
    {
        bool connection = r[2] == 0x41; // receiver: device connected / disconnected
        bool battery = r[0] == HidppChannel.LongReportId && (r[3] & 0x0F) == 0 &&
                       state.BatteryFeature.TryGetValue(r[1], out var fi) && fi == r[2];
        if (!connection && !battery) return;

        // Debounce: at most one early refresh every 3 s, 1.5 s after the event so the device settles.
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastEventRefreshTicks) < 3000) return;
        Interlocked.Exchange(ref _lastEventRefreshTicks, now);
        Task.Delay(1500).ContinueWith(_ => { if (!_disposed) _wake.Set(); });
    }

    void CloseEndpoint(string key)
    {
        if (_endpoints.Remove(key, out var state))
        {
            state.Channel.Dispose();
            Log?.Invoke($"Closed {state.Channel.Name}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        foreach (var key in _endpoints.Keys.ToList()) CloseEndpoint(key);
    }

    sealed class EndpointState(HidppChannel channel, HidppDiscovery.Endpoint endpoint)
    {
        public HidppChannel Channel { get; } = channel;
        public bool? IsDirect { get; set; }
        public Dictionary<byte, HidppDevice> Devices { get; } = new();
        public Dictionary<byte, (string Name, DeviceKind Kind)> Identity { get; } = new();
        // Read by the HID reader thread, written by the monitor thread: replace-only access is fine for our use.
        public System.Collections.Concurrent.ConcurrentDictionary<byte, byte> BatteryFeature { get; } = new();

        public string Connection { get; } = endpoint.ProductId switch
        {
            0xC52B or 0xC532 => "Unifying",
            0xC548 => "Bolt",
            0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D => "LIGHTSPEED",
            _ => endpoint.Name,
        };
    }
}
