using System.Globalization;
using System.Text.RegularExpressions;
using BatSignal.Core.Hidpp;
using BatSignal.Core.Monitoring;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration.Pnp;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace BatSignal.Core.Bluetooth;

/// <summary>
/// Battery for paired Bluetooth devices of any brand. Connected Bluetooth LE devices are read
/// live from the standard GATT Battery service (0x180F). Otherwise we use the last value Windows
/// stored on the device node (what Settings › Bluetooth &amp; devices shows), which also covers
/// classic Bluetooth headsets that report battery over the hands-free profile.
/// </summary>
public sealed partial class BluetoothBatteryProvider : IBatteryProvider
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string NameKey = "System.ItemNameDisplay";
    const string BluetoothClassGuid = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public event Action<string>? Log;

    public IReadOnlyList<DeviceSnapshot> Scan()
    {
        IReadOnlyList<PnpObject> nodes;
        try
        {
            nodes = Wait(PnpObject.FindAllAsync(PnpObjectType.Device, [BatteryKey, NameKey],
                $"System.Devices.ClassGuid:=\"{BluetoothClassGuid}\""));
        }
        catch (Exception e)
        {
            Log?.Invoke($"Bluetooth enumeration failed: {e.Message}");
            return [];
        }

        if (Environment.GetEnvironmentVariable("BATSIGNAL_DEBUG_BT") == "1")
            foreach (var n in nodes) Log?.Invoke($"node {n.Id} battery={ReadByte(n, BatteryKey)} name={ReadString(n, NameKey)}");

        // Bluetooth LE devices that expose a Battery service have a child node "BTHLEDEVICE\{0000180F-...}_..._<address>".
        var withBatteryService = nodes
            .Select(n => BatteryServiceRegex().Match(n.Id))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .ToHashSet();

        // WinRT ids of paired devices on every radio, by address (FromBluetoothAddressAsync only checks the default radio).
        var leIds = PairedIds(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        var classicIds = PairedIds(BluetoothDevice.GetDeviceSelectorFromPairingState(true));

        var result = new List<DeviceSnapshot>();
        foreach (var node in nodes)
        {
            var m = DeviceNodeRegex().Match(node.Id);
            if (!m.Success) continue;
            bool le = m.Groups[1].Value.Equals("BTHLE", StringComparison.OrdinalIgnoreCase);
            string address = m.Groups[2].Value.ToUpperInvariant();
            byte? stored = ReadByte(node, BatteryKey);

            if (stored is null && !(le && withBatteryService.Contains(address)))
                continue; // no way to know this device's battery

            try
            {
                var snap = (le ? ReadLowEnergy(address, leIds.GetValueOrDefault(address), stored)
                               : ReadClassic(address, classicIds.GetValueOrDefault(address), stored))
                           ?? Snapshot(address, "", DeviceKind.Unknown, false, stored, false); // could not open it: stored value only
                var name = ReadString(node, NameKey) ?? snap.Name;
                result.Add(snap with { Name = name, Kind = snap.Kind == DeviceKind.Unknown ? KindFromName(name) : snap.Kind });
            }
            catch (Exception e)
            {
                Log?.Invoke($"Bluetooth {address}: {e.Message}");
            }
        }
        return result;
    }

    DeviceSnapshot? ReadLowEnergy(string address, string? id, byte? stored)
    {
        using var dev = Wait(id is not null ? BluetoothLEDevice.FromIdAsync(id) : BluetoothLEDevice.FromBluetoothAddressAsync(ParseAddress(address)));
        if (dev is null) return null;
        bool connected = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
        byte? live = connected ? ReadGattBattery(dev) : null;

        var kind = DeviceKind.Unknown;
        if (dev.Appearance?.Category == BluetoothLEAppearanceCategories.HumanInterfaceDevice)
        {
            var sub = dev.Appearance.SubCategory;
            if (sub == BluetoothLEAppearanceSubcategories.Keyboard) kind = DeviceKind.Keyboard;
            else if (sub == BluetoothLEAppearanceSubcategories.Mouse) kind = DeviceKind.Mouse;
            else if (sub == BluetoothLEAppearanceSubcategories.Gamepad || sub == BluetoothLEAppearanceSubcategories.Joystick) kind = DeviceKind.Gamepad;
            else if (sub == BluetoothLEAppearanceSubcategories.DigitizerTablet) kind = DeviceKind.Touchpad;
        }
        return Snapshot(address, dev.Name, kind, connected, live ?? stored, live is not null);
    }

    DeviceSnapshot? ReadClassic(string address, string? id, byte? stored)
    {
        using var dev = Wait(id is not null ? BluetoothDevice.FromIdAsync(id) : BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(address)));
        if (dev is null) return null;
        bool connected = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
        var cod = dev.ClassOfDevice;
        var kind = cod.MajorClass switch
        {
            BluetoothMajorClass.AudioVideo => DeviceKind.Headset,
            BluetoothMajorClass.Peripheral => cod.MinorClass switch
            {
                BluetoothMinorClass.PeripheralGamepad or BluetoothMinorClass.PeripheralJoystick => DeviceKind.Gamepad,
                BluetoothMinorClass.PeripheralDigitizerTablet => DeviceKind.Touchpad,
                _ => DeviceKind.Unknown, // keyboard / pointing are bit flags in the minor class; the name decides
            },
            _ => DeviceKind.Unknown,
        };
        return Snapshot(address, dev.Name, kind, connected, stored, false);
    }

    static byte? ReadGattBattery(BluetoothLEDevice dev)
    {
        var services = Wait(dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached));
        if (services.Status != GattCommunicationStatus.Success) return null;
        try
        {
            foreach (var service in services.Services)
            {
                var chars = Wait(service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached));
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics)
                {
                    var read = Wait(c.ReadValueAsync(BluetoothCacheMode.Uncached));
                    if (read.Status == GattCommunicationStatus.Success && read.Value.Length > 0)
                        return DataReader.FromBuffer(read.Value).ReadByte();
                }
            }
            return null;
        }
        finally
        {
            foreach (var s in services.Services) s.Dispose();
        }
    }

    static DeviceSnapshot Snapshot(string address, string name, DeviceKind kind, bool connected, byte? percent, bool live) => new()
    {
        Id = $"BT/{address}",
        Name = name,
        Kind = kind,
        Connection = "Bluetooth",
        Online = connected,
        LastSeen = connected ? DateTimeOffset.Now : null,
        Battery = percent is byte p
            ? new BatteryInfo(p, p >= 90 ? BatteryLevel.Full : p >= 30 ? BatteryLevel.Good : p >= 10 ? BatteryLevel.Low : BatteryLevel.Critical,
                ChargeState.Unknown, null, false, false, live ? "Bluetooth GATT battery" : "Windows Bluetooth battery")
            : null,
    };

    static DeviceKind KindFromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("key")) return DeviceKind.Keyboard;
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere") || Regex.IsMatch(n, @"\bm\d{3}\b")) return DeviceKind.Mouse;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense")) return DeviceKind.Gamepad;
        if (n.Contains("bud") || n.Contains("head") || n.Contains("airpod") || n.Contains("ear") || n.Contains("wh-") || n.Contains("wf-")) return DeviceKind.Headset;
        return DeviceKind.Unknown;
    }

    /// <summary>Maps "D51275F024D9" to the WinRT id "BluetoothLE#BluetoothLE&lt;radio&gt;-d5:12:75:f0:24:d9".</summary>
    Dictionary<string, string> PairedIds(string selector)
    {
        try
        {
            return Wait(Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector))
                .Select(d => (d.Id, Address: d.Id[(d.Id.LastIndexOf('-') + 1)..].Replace(":", "").ToUpperInvariant()))
                .Where(x => x.Address.Length == 12)
                .GroupBy(x => x.Address)
                .ToDictionary(g => g.Key, g => g.First().Id);
        }
        catch (Exception e)
        {
            Log?.Invoke($"Bluetooth paired-device lookup failed: {e.Message}");
            return new();
        }
    }

    static byte? ReadByte(PnpObject node, string key) =>
        node.Properties.TryGetValue(key, out var value) && value is byte b ? b : null;

    static string? ReadString(PnpObject node, string key) =>
        node.Properties.TryGetValue(key, out var value) && value is string s && s.Length > 0 ? s : null;

    static ulong ParseAddress(string hex) => ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    static T Wait<T>(IAsyncOperation<T> op) => op.AsTask().WaitAsync(Timeout).GetAwaiter().GetResult();

    [GeneratedRegex(@"^(BTHLE|BTHENUM)\\DEV_([0-9A-F]{12})\\", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceNodeRegex();

    [GeneratedRegex(@"^BTHLEDEVICE\\\{0000180F-0000-1000-8000-00805F9B34FB\}_.*_([0-9A-F]{12})\\", RegexOptions.IgnoreCase)]
    private static partial Regex BatteryServiceRegex();
}
