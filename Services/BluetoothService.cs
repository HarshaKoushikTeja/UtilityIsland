using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Services;

/// <summary>Announces Bluetooth devices as they connect (headphones, controllers, ...).</summary>
internal sealed class BluetoothService
{
    private DeviceWatcher? _watcher;
    private bool _ready;

    /// <summary>Raised on a background thread with the device name.</summary>
    public event Action<string>? Connected;

    public void Start()
    {
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _watcher = DeviceInformation.CreateWatcher(selector);

            _watcher.Added += (_, device) =>
            {
                // Devices already connected at launch arrive before EnumerationCompleted; skip those.
                if (_ready && !string.IsNullOrWhiteSpace(device.Name))
                {
                    Connected?.Invoke(device.Name);
                }
            };
            // The watcher only keeps running if all three events have handlers.
            _watcher.Updated += (_, _) => { };
            _watcher.Removed += (_, _) => { };
            _watcher.EnumerationCompleted += (_, _) => _ready = true;

            _watcher.Start();
        }
        catch
        {
            // no Bluetooth radio
        }
    }

    public void Stop()
    {
        try { _watcher?.Stop(); } catch { }
    }
}
