namespace BatSignal.Core.Monitoring;

/// <summary>
/// A source of battery readings other than Logitech receivers (Bluetooth, XInput, HID gamepads).
/// <see cref="Scan"/> runs on the monitor thread and returns the devices it can see right now;
/// devices missing from a scan are kept with their last reading and marked asleep.
/// </summary>
public interface IBatteryProvider
{
    event Action<string>? Log;

    IReadOnlyList<DeviceSnapshot> Scan();
}
