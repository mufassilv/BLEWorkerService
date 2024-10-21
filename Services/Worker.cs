using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using BLEWorkerService.Models;
using Microsoft.Extensions.DependencyInjection;
using Windows.Devices.Enumeration;

namespace BLEWorkerService.Services
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private BluetoothLEDevice _bluetoothLeDevice;
        private GattCharacteristic _characteristicsWrite;
        private GattCharacteristic _characteristicsNotify;
        private BluetoothLEAdvertisementWatcher _bleWatcher;
        private readonly IServiceProvider _serviceProvider;
        private WebSocketServer _webSocketServer;
        private bool _isActive = false;
        private Timer _statusCheckTimer;

        private TaskCompletionSource<bool> _scanCompletionSource;
        private const int ScanTimeoutMilliseconds = 10000; // 10 seconds scan timeout

        // UUIDs for the pen device
        private Guid PEN_SERVICE_UUID = new Guid("000019f1-0000-1000-8000-00805f9b34fb");
        private Guid PEN_CHARACTERISTICS_WRITE_UUID = new Guid("00002ba0-0000-1000-8000-00805f9b34fb");
        private Guid PEN_CHARACTERISTICS_NOTIFICATION_UUID = new Guid("00002ba1-0000-1000-8000-00805f9b34fb");

        private HashSet<ulong> _scannedDeviceAddresses = new HashSet<ulong>();
        private Dictionary<ulong, string> _deviceNames = new Dictionary<ulong, string>();

        public Worker(ILogger<Worker> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting Bluetooth LE advertisement watcher...");
            // Lazily resolve WebSocketServer when needed
            _webSocketServer = _serviceProvider.GetRequiredService<WebSocketServer>();

            // Initialize the status check timer
            _statusCheckTimer = new Timer(CheckDeviceStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

            return Task.CompletedTask;
        }

        public void Activate()
        {
            if (!_isActive)
            {
                _isActive = true;
                _logger.LogInformation("Worker service activated");
                InitializeBluetoothWatcher();
                ScanForPairedDevices();
            }
        }

        public void Deactivate()
        {
            if (_isActive)
            {
                _isActive = false;
                _logger.LogInformation("Worker service deactivated");
                StopBluetoothWatcher();
                Disconnect();
            }
        }

        public async Task<List<string>> ScanDevicesAsync()
        {
            if (!_isActive)
            {
                _logger.LogWarning("Cannot scan for devices: Worker service is not active");
                return new List<string>();
            }

            _logger.LogInformation("Starting device scan...");
            _scanCompletionSource = new TaskCompletionSource<bool>();

            // Clear previous scan results
            _scannedDeviceAddresses.Clear();
            _deviceNames.Clear();

            // Start the watcher if it's not already running
            if (_bleWatcher == null || _bleWatcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                InitializeBluetoothWatcher();
            }

            // Wait for the scan to complete or timeout
            await Task.WhenAny(_scanCompletionSource.Task, Task.Delay(ScanTimeoutMilliseconds));

            // Stop the watcher
            _bleWatcher.Stop();

            _logger.LogInformation($"Scan completed. Found {_deviceNames.Count} devices.");

            // Return the list of device names
            return _deviceNames.Values.ToList();
        }

        private void InitializeBluetoothWatcher()
        {
            _bleWatcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _bleWatcher.Received += OnAdvertisementReceived;
            _bleWatcher.Stopped += OnWatcherStopped;
            _bleWatcher.Start();

            _logger.LogInformation("Bluetooth LE watcher started.");
        }

        private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            _scanCompletionSource?.TrySetResult(true);
        }

        private void StopBluetoothWatcher()
        {
            if (_bleWatcher != null)
            {
                _bleWatcher.Stop();
                _bleWatcher.Received -= OnAdvertisementReceived;
                _bleWatcher = null;
                _logger.LogInformation("Bluetooth LE watcher stopped.");
            }
        }

        private async void ScanForPairedDevices()
        {
            try
            {
                string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var deviceInfoCollection = await DeviceInformation.FindAllAsync(selector);

                foreach (var deviceInfo in deviceInfoCollection)
                {
                    var device = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                    if (device != null)
                    {
                        _scannedDeviceAddresses.Add(device.BluetoothAddress);
                        _deviceNames[device.BluetoothAddress] = device.Name;
                        _logger.LogInformation($"Found paired device: {device.Name}");
                    }
                }

                // Attempt to connect to the first paired device
                if (_deviceNames.Any())
                {
                    await ConnectToDeviceAsync(_deviceNames.First().Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error scanning for paired devices: {ex.Message}");
            }
        }

        private async void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (!_isActive) return;

            try
            {
                // Check if the device has already been scanned
                if (_scannedDeviceAddresses.Contains(args.BluetoothAddress))
                {
                    return;
                }

                _logger.LogInformation($"Advertisement received from device: {args.Advertisement.LocalName}");

                // Check for the pen service UUID
                if (args.Advertisement.ServiceUuids.Contains(PEN_SERVICE_UUID))
                {
                    var device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                    if (device != null)
                    {
                        _scannedDeviceAddresses.Add(args.BluetoothAddress);
                        _deviceNames[args.BluetoothAddress] = device.Name;

                        _logger.LogInformation($"Compatible device found: {device.Name} - Address: {args.BluetoothAddress}");

                        // Send updated device list to clients
                        var deviceList = _deviceNames.Values.ToList();
                        await _webSocketServer.SendScannedDeviceList(deviceList);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during device discovery: {ex.Message}");
            }
        }

        public async Task ConnectToDeviceAsync(string deviceName)
        {
            if (!_isActive)
            {
                _logger.LogWarning("Cannot connect to device: Worker service is not active");
                return;
            }
            try
            {
                var deviceEntry = _deviceNames.FirstOrDefault(x => x.Value == deviceName);
                if (deviceEntry.Key != 0)
                {
                    // Disconnect existing device if any
                    Disconnect();

                    _bluetoothLeDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(deviceEntry.Key);
                    if (_bluetoothLeDevice != null)
                    {
                        await InitializeCharacteristicsAsync();
                        await _webSocketServer.SendDeviceStatus($"Connected to device: {_bluetoothLeDevice.Name}");
                        _logger.LogInformation($"Successfully connected to device: {_bluetoothLeDevice.Name}");
                    }
                    else
                    {
                        await _webSocketServer.SendDeviceStatus("Failed to connect to device");
                        _logger.LogWarning($"Failed to connect to device: {deviceName}");
                    }
                }
                else
                {
                    await _webSocketServer.SendDeviceStatus($"Device not found: {deviceName}");
                    _logger.LogWarning($"Device not found in scanned list: {deviceName}");
                }
            }
            catch (Exception ex)
            {
                await _webSocketServer.SendDeviceStatus($"Connection error: {ex.Message}");
                _logger.LogError($"Error connecting to device: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            try
            {
                if (_characteristicsNotify != null)
                {
                    _characteristicsNotify.ValueChanged -= NotifyCharacteristic_ValueChanged;
                }

                if (_bluetoothLeDevice != null)
                {
                    _bluetoothLeDevice.Dispose();
                    _bluetoothLeDevice = null;
                    _characteristicsWrite = null;
                    _characteristicsNotify = null;

                    _logger.LogInformation("Device disconnected successfully");
                    _webSocketServer.SendDeviceStatus("Device disconnected").Wait();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during device disconnect: {ex.Message}");
            }
        }
        private async void CheckDeviceStatus(object state)
        {
            if (_isActive && _bluetoothLeDevice != null)
            {
                var status = _bluetoothLeDevice.ConnectionStatus == BluetoothConnectionStatus.Connected
                    ? "Connected"
                    : "Disconnected";
                await _webSocketServer.SendDeviceStatus($"Device status: {status}");
            }
        }
        private async Task InitializeCharacteristicsAsync()
        {
            try
            {
                var gattServicesResult = await _bluetoothLeDevice.GetGattServicesForUuidAsync(PEN_SERVICE_UUID);
                if (gattServicesResult.Status == GattCommunicationStatus.Success)
                {
                    var service = gattServicesResult.Services.FirstOrDefault();
                    if (service != null)
                    {
                        var characteristicsResult = await service.GetCharacteristicsAsync();
                        if (characteristicsResult.Status == GattCommunicationStatus.Success)
                        {
                            foreach (var characteristic in characteristicsResult.Characteristics)
                            {
                                if (characteristic.Uuid == PEN_CHARACTERISTICS_WRITE_UUID)
                                {
                                    _characteristicsWrite = characteristic;
                                    _logger.LogInformation("Write characteristic initialized");
                                }
                                else if (characteristic.Uuid == PEN_CHARACTERISTICS_NOTIFICATION_UUID)
                                {
                                    _characteristicsNotify = characteristic;
                                    _characteristicsNotify.ValueChanged += NotifyCharacteristic_ValueChanged;

                                    var status = await _characteristicsNotify.WriteClientCharacteristicConfigurationDescriptorAsync(
                                        GattClientCharacteristicConfigurationDescriptorValue.Notify);

                                    if (status == GattCommunicationStatus.Success)
                                    {
                                        _logger.LogInformation("Notification characteristic initialized and enabled");
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Failed to enable notifications");
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Failed to get characteristics: {characteristicsResult.Status}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No service found for the specified UUID");
                    }
                }
                else
                {
                    _logger.LogWarning($"Failed to get services: {gattServicesResult.Status}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error initializing characteristics: {ex.Message}");
                throw;
            }
        }

        private async void NotifyCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                _logger.LogInformation("Notification received from pen");

                var data = args.CharacteristicValue.ToArray();

                // Parse stroke data - assuming 16-bit integers for X and Y
                int x = BitConverter.ToInt16(data, 0);
                int y = BitConverter.ToInt16(data, 2);

                _logger.LogDebug($"Stroke Data: X={x}, Y={y}");

                var strokeData = new StrokeData(x, y);
                await _webSocketServer.SendStrokeData(strokeData);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing pen notification: {ex.Message}");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                Deactivate();
                _statusCheckTimer?.Dispose();
                _logger.LogInformation("Stopped Bluetooth scanning and closed connections");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during service shutdown: {ex.Message}");
            }

            await base.StopAsync(cancellationToken);
        }

        // Optional: Method to write data to the pen if needed
        public async Task WriteDataToPenAsync(byte[] data)
        {
            if (!_isActive)
            {
                _logger.LogWarning("Cannot write to pen: Worker service is not active");
                return;
            }

            try
            {
                if (_characteristicsWrite != null && _bluetoothLeDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    var writeResult = await _characteristicsWrite.WriteValueAsync(data.AsBuffer());
                    if (writeResult == GattCommunicationStatus.Success)
                    {
                        _logger.LogInformation("Data written to pen successfully");
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to write data to pen: {writeResult}");
                    }
                }
                else
                {
                    _logger.LogWarning("Cannot write to pen - device not connected or characteristic not initialized");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error writing data to pen: {ex.Message}");
                throw;
            }
        }
    }
}