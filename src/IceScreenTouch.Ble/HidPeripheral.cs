// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using IceScreenTouch.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace IceScreenTouch.Ble;

/// <summary>Advertising state of one published GATT service.</summary>
/// <param name="Name">Short name of the service (HID, Battery).</param>
/// <param name="Uuid">Service UUID.</param>
/// <param name="Status">Current advertisement status reported by Windows.</param>
public sealed record GattServiceState(string Name, Guid Uuid, GattServiceProviderAdvertisementStatus Status)
{
    /// <summary>True when Windows is advertising the service.</summary>
    public bool IsAdvertising =>
        Status is GattServiceProviderAdvertisementStatus.Started
            or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;
}

/// <summary>Counters of the notifications sent so far.</summary>
/// <param name="Sent">Reports handed to Windows while at least one client was subscribed.</param>
/// <param name="Skipped">Reports dropped because nobody was subscribed.</param>
/// <param name="Failed">Notifications that Windows reported as not delivered (per client).</param>
/// <param name="Busy">Reports not sent to a client that was still busy with an earlier one (it gets the latest one later).</param>
public readonly record struct HidSendStats(long Sent, long Skipped, long Failed, long Busy = 0);

/// <summary>
/// HID over GATT peripheral: publishes the HID service (0x1812) with a relative mouse (report ID 1)
/// and a 2-finger absolute digitizer (report ID 2), and sends input reports as notifications.
/// Port of HidPeripheral in poc/poc2-ble-hid/ble_hid_poc.py. The services and characteristics are created
/// in the same order and with the same values as the PoC, because the iPhone caches the GATT layout.
/// </summary>
/// <remarks>Only one process at a time can publish the HID service; a second one gets Aborted.</remarks>
public sealed class HidPeripheral : ITouchSink, IHomeButtonSink, IKeyboardSink, IAsyncDisposable
{
    private static readonly Guid DeviceInformationService = BluetoothUuidHelper.FromShortId(0x180A);
    private static readonly Guid BatteryService = BluetoothUuidHelper.FromShortId(0x180F);
    private static readonly Guid HidService = BluetoothUuidHelper.FromShortId(0x1812);

    private static readonly Guid ManufacturerName = BluetoothUuidHelper.FromShortId(0x2A29);
    private static readonly Guid ModelNumber = BluetoothUuidHelper.FromShortId(0x2A24);
    private static readonly Guid BatteryLevel = BluetoothUuidHelper.FromShortId(0x2A19);
    private static readonly Guid HidInformation = BluetoothUuidHelper.FromShortId(0x2A4A);
    private static readonly Guid ReportMap = BluetoothUuidHelper.FromShortId(0x2A4B);
    private static readonly Guid HidControlPoint = BluetoothUuidHelper.FromShortId(0x2A4C);
    private static readonly Guid Report = BluetoothUuidHelper.FromShortId(0x2A4D);
    private static readonly Guid ProtocolModeUuid = BluetoothUuidHelper.FromShortId(0x2A4E);
    private static readonly Guid ReportReference = BluetoothUuidHelper.FromShortId(0x2908);

    private readonly List<(string Name, GattServiceProvider Provider)> _providers = [];
    private readonly Dictionary<byte, GattLocalCharacteristic> _reports = [];
    private static readonly TimeSpan SendDeadline = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SlowNotify = TimeSpan.FromMilliseconds(500);

    // How many failed notifications in a row are followed by a resend of the latest report.
    private const int MaxRetries = 1;

    // Reading a new device's name: how many times, and how far apart.
    private const int NameAttempts = 3;
    private static readonly TimeSpan NameRetryDelay = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<(byte ReportId, string Id), ClientState> _clients = [];
    private readonly Action<string> _log;
    private readonly List<string> _notes = [];

    private byte[] _lastMouse = HidReports.EncodeMouse();
    private byte[] _lastTouch = HidReports.EncodeTouch([]);
    private byte[] _lastConsumer = HidReports.EncodeConsumer();
    private byte[] _lastKeyboard = HidReports.EncodeKeyboard();
    private volatile byte _protocolMode = 1; // 1 = Report protocol
    private volatile byte _controlPoint = 1; // 0 = Suspend, 1 = Exit Suspend
    private long _sent;
    private long _skipped;
    private long _failed;
    private long _busy;
    private bool _disposed;
    private ConnectionTuner? _tuner;
    private PairedSubscriberGate? _pairing;

    // SubscribedClientsChanged of the four reports can run at the same time: a snapshot of the subscribers and what is
    // done with it (the pairing gate, the client order, the tuner) must not be overtaken by an older snapshot.
    private readonly Lock _subscribersLock = new();

    // The connected devices: when each subscribed to the touch report, and its Bluetooth name once known.
    private readonly Lock _clientsLock = new();
    private readonly Dictionary<string, long> _order = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _models = new(StringComparer.Ordinal);
    private long _nextOrder;

    // The one device that gets the reports (null with _targeted = everyone, for the tools). Guarded by _sendLock.
    // A new target takes over only between gestures: while a finger or a key is down on the old one, it gets the
    // rest of that gesture (a lift sent elsewhere would leave the finger down on it).
    private bool _targeted;
    private string? _target;
    private string? _pendingTarget;
    private readonly HashSet<byte> _activeReports = [];

    // The latest report of each ID handed to the clients, under the send lock (what a resend sends).
    private readonly Dictionary<byte, byte[]> _queued = [];

    private HidPeripheral(Action<string>? log)
    {
        _log = log ?? (_ => { });
    }

    /// <summary>Raised (on a thread-pool thread) when a client subscribes to or unsubscribes from a report.</summary>
    public event EventHandler? SubscriptionChanged;

    /// <summary>Raised (on a thread-pool thread) when the advertisement status of a service changes.</summary>
    public event EventHandler? AdvertisementStatusChanged;

    /// <summary>Things that were skipped while publishing (for example Device Information, which Windows reserves).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Protocol Mode last written by the host (1 = Report protocol, 0 = Boot protocol).</summary>
    public byte ProtocolMode => _protocolMode;

    /// <summary>HID Control Point last written by the host (0 = Suspend, 1 = Exit Suspend).</summary>
    public byte ControlPoint => _controlPoint;

    /// <inheritdoc/>
    public bool IsSubscribed => SubscribedCount(HidConstants.TouchReportId) > 0;

    /// <summary>The report map has consumer control keys (report ID 3).</summary>
    public bool ConsumerControl { get; private init; }

    /// <summary>
    /// Presses and releases a consumer control key (for example <see cref="HidConstants.ConsumerAcHome"/>).
    /// Needs <see cref="ConsumerControl"/>.
    /// </summary>
    public async ValueTask PressConsumerKeyAsync(ushort usage, TimeSpan? hold = null, CancellationToken cancellationToken = default)
    {
        if (!ConsumerControl)
        {
            throw new InvalidOperationException("the report map has no consumer control keys");
        }

        try
        {
            _lastConsumer = HidReports.EncodeConsumer(usage);
            await NotifyAsync(HidConstants.ConsumerReportId, _lastConsumer, usage == 0, cancellationToken).ConfigureAwait(false);
            await Task.Delay(hold ?? TimeSpan.FromMilliseconds(60), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release even when cancelled: a Menu key left down is a long press on the device (Siri), and the
            // active report would keep the input target from changing.
            _lastConsumer = HidReports.EncodeConsumer();
            if (!_disposed)
            {
                await ReleaseSend.RunAsync(ct => new ValueTask(NotifyAsync(HidConstants.ConsumerReportId, _lastConsumer, neutral: true, ct)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The report map has a keyboard (report ID 4; implies <see cref="ConsumerControl"/>).</summary>
    public bool Keyboard { get; private init; }

    /// <inheritdoc/>
    public bool HasKeyboard => Keyboard;

    /// <summary>Sends one keyboard report: <paramref name="modifiers"/> and the keys held down (none = all released).</summary>
    public async ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken = default)
    {
        if (!Keyboard)
        {
            throw new InvalidOperationException("the report map has no keyboard");
        }

        var payload = HidReports.EncodeKeyboard(modifiers, keys.Span);
        _lastKeyboard = payload;
        var neutral = modifiers == 0 && !keys.Span.ContainsAnyExcept((byte)0);
        await NotifyAsync(HidConstants.KeyboardReportId, payload, neutral, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Presses and releases one key (<see cref="KeyboardTyping.PressAsync"/>).</summary>
    public ValueTask PressKeyAsync(HidKey key, TimeSpan? hold = null, CancellationToken cancellationToken = default) =>
        this.PressAsync(key, hold, cancellationToken: cancellationToken);

    /// <summary>Types <paramref name="text"/> key by key (<see cref="KeyboardTyping.TypeAsync"/>); returns the characters skipped.</summary>
    public ValueTask<string> TypeAsync(string text, HidKeyboardLayout layout, TimeSpan? interval = null, CancellationToken cancellationToken = default) =>
        KeyboardTyping.TypeAsync(this, text, layout, interval, cancellationToken: cancellationToken);

    /// <inheritdoc/>
    public bool HasHomeButton => ConsumerControl;

    /// <inheritdoc/>
    /// <remarks>The consumer "Menu" key (<see cref="HidConstants.ConsumerMenu"/>).</remarks>
    public ValueTask PressHomeAsync(CancellationToken cancellationToken) =>
        PressConsumerKeyAsync(HidConstants.ConsumerMenu, cancellationToken: cancellationToken);

    /// <summary>Notification counters.</summary>
    public HidSendStats Stats => new(Interlocked.Read(ref _sent), Interlocked.Read(ref _skipped), Interlocked.Read(ref _failed), Interlocked.Read(ref _busy));

    /// <summary>
    /// Publishes the GATT services (not advertised yet; call <see cref="StartAdvertising"/>).
    /// The report map has the consumer keys (the home button, since 2026-11-06) and the keyboard (text input, since
    /// 2026-11-06; <see cref="HidReports.KeyboardReportMap"/>), so devices paired before must
    /// pair again once. Device Information and Battery are best effort, like in the PoC. Throws
    /// <see cref="BleException"/> when the HID service cannot be created.
    /// </summary>
    public static Task<HidPeripheral> CreateAsync(Action<string>? log = null, CancellationToken cancellationToken = default) =>
        CreateAsync(consumerControl: true, keyboard: true, log, cancellationToken);

    /// <summary>
    /// <inheritdoc cref="CreateAsync(Action{string}?, CancellationToken)"/> With <paramref name="consumerControl"/>,
    /// the report map also has consumer control keys (<see cref="HidReports.ExtendedReportMap"/>, for the home
    /// button): a different GATT layout, so paired devices must pair again.
    /// </summary>
    public static Task<HidPeripheral> CreateAsync(bool consumerControl, Action<string>? log = null, CancellationToken cancellationToken = default) =>
        CreateAsync(consumerControl, keyboard: false, log, cancellationToken);

    /// <summary>
    /// <inheritdoc cref="CreateAsync(bool, Action{string}?, CancellationToken)"/> With <paramref name="keyboard"/>,
    /// the report map also has a keyboard (<see cref="HidReports.KeyboardReportMap"/>, the default; needs
    /// <paramref name="consumerControl"/>).
    /// </summary>
    public static async Task<HidPeripheral> CreateAsync(bool consumerControl, bool keyboard, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (keyboard && !consumerControl)
        {
            throw new ArgumentException("the keyboard report map includes the consumer keys", nameof(keyboard));
        }

        var peripheral = new HidPeripheral(log) { ConsumerControl = consumerControl, Keyboard = keyboard };
        peripheral._tuner = new ConnectionTuner(peripheral._log);
        peripheral._pairing = new PairedSubscriberGate(peripheral.LookUpPairedAsync, peripheral._log);
        peripheral._pairing.PairedDeviceAdded += (_, _) => peripheral.OnPairedDeviceAdded();
        try
        {
            await peripheral.BuildAsync(cancellationToken).ConfigureAwait(false);
            return peripheral;
        }
        catch
        {
            await peripheral.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts advertising every published service (connectable and discoverable).</summary>
    public void StartAdvertising()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var (_, provider) in _providers)
        {
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true,
            });
        }
    }

    /// <summary>Stops advertising every service. Connected clients may stay connected.</summary>
    public void StopAdvertising()
    {
        foreach (var (_, provider) in _providers)
        {
            if (provider.AdvertisementStatus is not (GattServiceProviderAdvertisementStatus.Stopped or GattServiceProviderAdvertisementStatus.Created))
            {
                provider.StopAdvertising();
            }
        }
    }

    /// <summary>Advertising state of each published service.</summary>
    public IReadOnlyList<GattServiceState> GetServiceStates() =>
        [.. _providers.Select(p => new GattServiceState(p.Name, p.Provider.Service.Uuid, p.Provider.AdvertisementStatus))];

    /// <summary>Number of clients subscribed to the input report <paramref name="reportId"/>.</summary>
    /// <remarks>Only paired devices count (<see cref="PairedSubscriberGate"/>).</remarks>
    public int SubscribedCount(byte reportId) =>
        _reports.TryGetValue(reportId, out var ch) ? PairedClients(ch).Count : 0;

    /// <summary>Device IDs of the paired clients subscribed to the touch report.</summary>
    public IReadOnlyList<string> SubscribedDevices() =>
        _reports.TryGetValue(HidConstants.TouchReportId, out var ch)
            ? [.. PairedClients(ch).Select(c => c.Session.DeviceId.Id)]
            : [];

    // The subscribers that may get input: a device that is not (yet) known to be paired gets nothing, whatever it
    // subscribed to. Cheap, since it runs for every report.
    private List<GattSubscribedClient> PairedClients(GattLocalCharacteristic ch) =>
        _pairing is { } gate ? [.. ch.SubscribedClients.Where(c => gate.IsPaired(c.Session.DeviceId.Id))] : [];

    // Every device subscribed to any input report, paired or not.
    private List<string> AllSubscriberIds() =>
        [.. _reports.Values.SelectMany(ch => ch.SubscribedClients).Select(c => c.Session.DeviceId.Id).Distinct(StringComparer.Ordinal)];

    // Whether Windows has the device as paired (bonded); null = could not be found out. The subscription descriptor
    // is created by Windows, so the pairing is checked here as well as through the protection levels.
    private async Task<bool?> LookUpPairedAsync(string id, CancellationToken ct)
    {
        // Exceptions go to the gate, which treats them as "not known" and tries again later.
        using var device = await BluetoothLEDevice.FromIdAsync(id).AsTask(ct).ConfigureAwait(false);
        return device?.DeviceInformation?.Pairing?.IsPaired;
    }

    // A subscriber turned out to be paired: it counts as a client from now on.
    private void OnPairedDeviceAdded()
    {
        if (_disposed)
        {
            return;
        }

        RefreshTouchClients();
        SubscriptionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshTouchClients()
    {
        lock (_subscribersLock)
        {
            if (_disposed)
            {
                return;
            }

            OnTouchSubscribersChanged();
            if (_tuner is { } tuner)
            {
                // Called inside the lock so that the requests reach the tuner in the order of their snapshots.
                _ = tuner.UpdateAsync(SubscribedDevices());
            }
        }
    }

    // New subscribers are checked for pairing first; they count once the check says so.
    private void OnSubscribersChanged(byte reportId)
    {
        lock (_subscribersLock)
        {
            if (_disposed)
            {
                return;
            }

            _pairing?.Update(AllSubscriberIds());
            if (reportId == HidConstants.TouchReportId)
            {
                RefreshTouchClients();
            }
        }
    }

    /// <summary>
    /// The devices subscribed to the touch report, with the order they subscribed in and their Bluetooth names
    /// (null until Windows told it; <see cref="SubscriptionChanged"/> is raised again then).
    /// </summary>
    public IReadOnlyList<HidClient> Clients()
    {
        var ids = SubscribedDevices();
        lock (_clientsLock)
        {
            return [.. ids.Select(id => new HidClient(id, _names.GetValueOrDefault(id), _order.GetValueOrDefault(id), _models.GetValueOrDefault(id)))];
        }
    }

    /// <summary>The device that gets the reports now (null = every subscribed device, until <see cref="SetTarget"/>).</summary>
    public string? Target
    {
        get
        {
            _sendLock.Wait();
            try
            {
                return _targeted ? _target : null;
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    /// <summary>
    /// Sends every later report to <paramref name="deviceId"/> only (a <see cref="HidClient.Id"/>; null = to nobody).
    /// While a finger or a key is down, the current target keeps getting reports until it is released.
    /// </summary>
    public void SetTarget(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sendLock.Wait();
        try
        {
            _targeted = true;
            _pendingTarget = deviceId;
            TakeNewTarget();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        var payload = HidReports.EncodeTouch(contacts.Span);
        _lastTouch = payload;
        var neutral = true;
        foreach (var c in contacts.Span)
        {
            neutral &= !c.Tip;
        }

        await NotifyAsync(HidConstants.TouchReportId, payload, neutral, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one relative mouse report (AssistiveTouch path). Movement is clamped to -127..127.</summary>
    public async ValueTask SendMouseAsync(int dx = 0, int dy = 0, int wheel = 0, int buttons = 0, CancellationToken cancellationToken = default)
    {
        var payload = HidReports.EncodeMouse(dx, dy, wheel, buttons);
        _lastMouse = payload;
        await NotifyAsync(HidConstants.MouseReportId, payload, buttons == 0, cancellationToken).ConfigureAwait(false);
    }

    // Called with the send lock held: the pending target takes over when nothing is held down on the current one
    // and it has no report still waiting to be resent.
    private void TakeNewTarget()
    {
        if (_pendingTarget == _target || _activeReports.Count > 0)
        {
            return;
        }

        foreach (var state in _clients.Values)
        {
            // Also while a notification to the current target is still on its way: if it fails (a lift, a key
            // release), it is resent only while that device is the target.
            if (state.DeviceId == _target && (state.Missed || state.Pending is { IsCompleted: false }))
            {
                return;
            }
        }

        _target = _pendingTarget;
        string? name;
        lock (_clientsLock)
        {
            name = _target is null ? null : _names.GetValueOrDefault(_target);
        }

        _log($"input goes to {(_target is null ? "no device" : name ?? LogPrivacy.LogId(_target))}");
    }

    private void OnTouchSubscribersChanged()
    {
        var ids = SubscribedDevices();
        List<string> added;
        lock (_clientsLock)
        {
            foreach (var gone in _order.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _order.Remove(gone);
            }

            added = [.. ids.Where(id => !_order.ContainsKey(id))];
            foreach (var id in added)
            {
                _order[id] = ++_nextOrder;
            }
        }

        foreach (var id in added)
        {
            _ = LookUpNameAsync(id);
        }
    }

    private async Task LookUpNameAsync(string id)
    {
        string? name;
        string? model;
        lock (_clientsLock)
        {
            name = _names.GetValueOrDefault(id);
            model = _models.GetValueOrDefault(id);
        }

        // Done only when both are known: a device whose model could not be read is asked again on its next
        // subscription (review, 2026-11-07).
        if (!string.IsNullOrWhiteSpace(name) && model is not null)
        {
            return;
        }

        // Windows may not know the name yet right after the device subscribed: try again a few times while it is
        // still subscribed (without a name, the device cannot be matched with the one the receiver shows). The model
        // is read in the same loop: a failed read of it is tried again like the name, also when the name is known.
        for (var attempt = 1; attempt <= NameAttempts && (string.IsNullOrWhiteSpace(name) || model is null); attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(NameRetryDelay).ConfigureAwait(false);
                bool subscribed;
                lock (_clientsLock)
                {
                    subscribed = _order.ContainsKey(id);
                }

                if (_disposed || !subscribed)
                {
                    return;
                }
            }

            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(id);
                if (device is not null && string.IsNullOrWhiteSpace(name))
                {
                    name = device.Name;
                    if (!string.IsNullOrWhiteSpace(name) && !_disposed)
                    {
                        lock (_clientsLock)
                        {
                            _names[id] = name;
                        }

                        SubscriptionChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                if (device is not null && !string.IsNullOrWhiteSpace(name) && model is null && !_disposed)
                {
                    model = await ReadModelAsync(device).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _log($"{LogPrivacy.LogId(id)}: cannot read its name or model ({ex.Message}){(attempt < NameAttempts ? "; trying again" : string.Empty)}");
            }
        }

        if (string.IsNullOrWhiteSpace(name) || _disposed)
        {
            return;
        }

        if (model is not null)
        {
            lock (_clientsLock)
            {
                _models[id] = model;
            }
        }

        _log($"{LogPrivacy.LogId(id)} is \"{name}\" ({model ?? "model unknown"})");
        SubscriptionChanged?.Invoke(this, EventArgs.Empty);
    }

    // The device's own Device Information service (we are its peripheral, and a GATT client of it at the same
    // time): an iPhone 15 Pro answers "iPhone16,1", an iPad Air (4th generation) "iPad13,1" (2026-11-06).
    private static async Task<string?> ReadModelAsync(BluetoothLEDevice device)
    {
        var services = await device.GetGattServicesForUuidAsync(DeviceInformationService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success)
        {
            return null;
        }

        try
        {
            foreach (var service in services.Services)
            {
                var characteristics = await service.GetCharacteristicsForUuidAsync(ModelNumber, BluetoothCacheMode.Uncached);
                foreach (var characteristic in characteristics.Characteristics)
                {
                    var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (read.Status == GattCommunicationStatus.Success && read.Value is { Length: > 0 } value)
                    {
                        return System.Text.Encoding.UTF8.GetString(value.ToArray()).TrimEnd('\0').Trim();
                    }
                }
            }
        }
        finally
        {
            foreach (var service in services.Services)
            {
                service.Dispose();
            }
        }

        return null;
    }

    /// <summary>Stops advertising and releases the services.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            StopAdvertising();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log($"stop advertising: {ex.Message}");
        }

        // Under the lock of the subscriber events, so that none of them runs against a disposed tuner or gate.
        lock (_subscribersLock)
        {
            _tuner?.Dispose();
            _pairing?.Dispose();
            _reports.Clear();
        }

        _providers.Clear();
        _clients.Clear();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    // One notification per subscribed client, started together. NotifyValueAsync(value) without a client waits for
    // every client, so one that stops answering (an iPhone whose Bluetooth was switched off while still listed as
    // subscribed) held every touch for about 8 s and froze the bridge (seen with an iPhone and an iPad, 2026-11-05).
    // Now the caller waits at most SendDeadline; a client still busy with an earlier report is skipped, and gets the
    // latest report of that ID once it is done (reports carry the whole state, so the latest one resynchronizes it).
    // A neutral payload holds nothing down (no finger, key or button): the target may change after it.
    private async Task NotifyAsync(byte reportId, byte[] payload, bool neutral, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ch = _reports[reportId];
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? all;
        try
        {
            if (_targeted)
            {
                TakeNewTarget();
            }

            if (neutral)
            {
                _activeReports.Remove(reportId);
            }
            else
            {
                _activeReports.Add(reportId);
            }

            // What a resend sends: the latest report that went through here (under the lock), not the _last* fields,
            // which the next caller writes before it waits for the lock (and maybe for a new target).
            _queued[reportId] = payload;

            // Paired devices only, also when everyone gets the reports (Probe, Bridge).
            var clients = _targeted
                ? [.. PairedClients(ch).Where(c => c.Session.DeviceId.Id == _target)]
                : PairedClients(ch);
            if (clients.Count == 0)
            {
                Interlocked.Increment(ref _skipped);
                return;
            }

            Interlocked.Increment(ref _sent);
            List<Task>? started = null;
            foreach (var client in clients)
            {
                var state = ClientStateFor(reportId, client);
                if (state.Pending is { IsCompleted: false } pending)
                {
                    state.Missed = true;
                    Interlocked.MemoryBarrier();
                    if (!pending.IsCompleted)
                    {
                        // OnNotified runs after the completion and will see Missed: it resends the latest report.
                        // A release skipped here goes first, or two taps would merge into one drag (and two presses
                        // of a key into one held key) when only the latest report is resent.
                        if (neutral)
                        {
                            state.SkippedRelease ??= payload;
                        }

                        Interlocked.Increment(ref _busy);
                        continue;
                    }

                    // It completed meanwhile; its OnNotified may have looked before Missed was set. Send now.
                }

                try
                {
                    (started ??= []).Add(StartNext(ch, reportId, client, state, payload));
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
                {
                    // The device is going away (Bluetooth off, unsubscribing). One device's failure must not end the
                    // bridge loop, which would stop every click until a restart.
                    Interlocked.Increment(ref _failed);
                    _log($"notify {LogPrivacy.LogId(state.DeviceId)}: {ex.Message}");
                }
            }

            all = started is null ? null : Task.WhenAll(started);
        }
        finally
        {
            _sendLock.Release();
        }

        if (all is not null)
        {
            await Task.WhenAny(all, Task.Delay(SendDeadline, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private ClientState ClientStateFor(byte reportId, GattSubscribedClient client)
    {
        var key = (reportId, client.Session.DeviceId.Id);
        if (!_clients.TryGetValue(key, out var state))
        {
            state = new ClientState(key.Id);
            _clients[key] = state;
        }

        return state;
    }

    // Called with the send lock held. A release skipped while the device was busy goes before the report (which then
    // follows as a resend: Missed is set again after the start, and OnNotified sends the latest queued report).
    private Task<GattClientNotificationResult> StartNext(GattLocalCharacteristic ch, byte reportId, GattSubscribedClient client, ClientState state, byte[] payload)
    {
        if (state.SkippedRelease is not { } release)
        {
            return StartNotify(ch, reportId, client, state, payload);
        }

        state.SkippedRelease = null;
        var task = StartNotify(ch, reportId, client, state, release);
        state.Missed = true;
        return task;
    }

    // Called with the send lock held.
    private Task<GattClientNotificationResult> StartNotify(GattLocalCharacteristic ch, byte reportId, GattSubscribedClient client, ClientState state, byte[] payload)
    {
        state.Missed = false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var task = ch.NotifyValueAsync(payload.AsBuffer(), client).AsTask();
        state.Pending = task;
        // Synchronously, before the sender's own await resumes (it was registered first): a failure must set Missed
        // before the next report's TakeNewTarget can switch away from this device and leave its lift unsent.
        // OnNotified is quick (the resend itself runs on the thread pool).
        _ = task.ContinueWith(t => OnNotified(t, ch, reportId, client, state, started), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private void OnNotified(Task<GattClientNotificationResult> task, GattLocalCharacteristic ch, byte reportId, GattSubscribedClient client, ClientState state, long started)
    {
        if (!task.IsCompletedSuccessfully || task.Result.Status != GattCommunicationStatus.Success)
        {
            Interlocked.Increment(ref _failed);

            // The lost report may have been a lift or a key release: resend the latest one, once (a device that is
            // gone keeps failing, and must not be retried for ever).
            if (state.Retries < MaxRetries)
            {
                state.Retries++;
                state.Missed = true;
            }
        }
        else
        {
            state.Retries = 0;
        }

        var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (took >= SlowNotify && !state.ReportedSlow)
        {
            state.ReportedSlow = true;
            _log($"note: a notification to {LogPrivacy.LogId(state.DeviceId)} took {took.TotalMilliseconds:0} ms; reports for it are skipped while it is busy (other devices are not held up)");
        }

        if (_disposed || !state.Missed)
        {
            return;
        }

        // It missed reports while busy: send it the latest one.
        _ = Task.Run(async () =>
        {
            try
            {
                await _sendLock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                // A skipped release still goes to a device that is no longer the target: it must not keep a finger down.
                if (!_disposed && state.Missed && state.Pending is not { IsCompleted: false }
                    && (!_targeted || state.DeviceId == _target || state.SkippedRelease is not null))
                {
                    var latest = state.DeviceId == _target || !_targeted ? _queued.GetValueOrDefault(reportId) ?? LastReport(reportId) : state.SkippedRelease!;
                    _ = StartNext(ch, reportId, client, state, latest);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
            {
                _log($"resend to {LogPrivacy.LogId(state.DeviceId)}: {ex.Message}");
            }
            finally
            {
                try
                {
                    _sendLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed meanwhile.
                }
            }
        });
    }

    private sealed class ClientState(string deviceId)
    {
        public string DeviceId { get; } = deviceId;

        public Task? Pending { get; set; }

        public bool Missed { get; set; }

        /// <summary>Failed notifications in a row that were resent.</summary>
        public int Retries { get; set; }

        /// <summary>The first release (finger up, keys up) skipped while busy; sent before anything else.</summary>
        public byte[]? SkippedRelease { get; set; }

        public bool ReportedSlow { get; set; }
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        // Device Information: the manufacturer only. No PnP ID: the one inherited from PoC 2 claimed another
        // company's USB vendor ID, and we have none of our own (review, 2026-11-07). Windows reserves 0x180A
        // (BluetoothError.DisabledByPolicy), so this is best effort only.
        try
        {
            var di = await CreateProviderAsync("Device Information", DeviceInformationService, ct).ConfigureAwait(false);
            await CreateStaticAsync(di, ManufacturerName, GattCharacteristicProperties.Read, "IceScreen Touch"u8.ToArray(), false, ct).ConfigureAwait(false);
        }
        catch (BleException ex)
        {
            Note($"Device Information service skipped: {ex.Message}");
        }

        // Battery: expected by most HOGP hosts (best effort as well).
        try
        {
            var bat = await CreateProviderAsync("Battery", BatteryService, ct).ConfigureAwait(false);
            await CreateStaticAsync(bat, BatteryLevel, GattCharacteristicProperties.Read, [100], false, ct).ConfigureAwait(false);
        }
        catch (BleException ex)
        {
            Note($"Battery service skipped: {ex.Message}");
        }

        var hid = await CreateProviderAsync("HID", HidService, ct).ConfigureAwait(false);
        await CreateStaticAsync(hid, HidInformation, GattCharacteristicProperties.Read, HidReports.HidInformation.ToArray(), true, ct).ConfigureAwait(false);
        var map = HidReports.ReportMapFor(ConsumerControl, Keyboard);
        await CreateStaticAsync(hid, ReportMap, GattCharacteristicProperties.Read, map.ToArray(), true, ct).ConfigureAwait(false);

        // HID Control Point (write without response).
        var cp = await CreateCharacteristicAsync(hid, HidControlPoint, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        }, "control point", ct).ConfigureAwait(false);
        cp.WriteRequested += async (_, args) => await HandleWriteAsync(args, v => _controlPoint = v).ConfigureAwait(false);

        // Protocol Mode (read / write without response).
        var pm = await CreateCharacteristicAsync(hid, ProtocolModeUuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        }, "protocol mode", ct).ConfigureAwait(false);
        pm.ReadRequested += async (_, args) => await HandleReadAsync(args, () => [_protocolMode]).ConfigureAwait(false);
        pm.WriteRequested += async (_, args) => await HandleWriteAsync(args, v => _protocolMode = v).ConfigureAwait(false);

        // One input Report characteristic per report ID, each with a Report Reference descriptor.
        byte[] reportIds = Keyboard
            ? [HidConstants.MouseReportId, HidConstants.TouchReportId, HidConstants.ConsumerReportId, HidConstants.KeyboardReportId]
            : ConsumerControl
            ? [HidConstants.MouseReportId, HidConstants.TouchReportId, HidConstants.ConsumerReportId]
            : [HidConstants.MouseReportId, HidConstants.TouchReportId];
        foreach (var reportId in reportIds)
        {
            var ch = await CreateCharacteristicAsync(hid, Report, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                // Meant for the subscription descriptor (CCCD), which Windows generates: a central should have to be
                // encrypted (paired) to subscribe. Microsoft documents this property for writes to the
                // characteristic; that Windows also applies it to the generated CCCD is relied upon, not documented,
                // and is verified on a device. PairedSubscriberGate is the second line of defence.
                WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
            }, $"report {reportId}", ct).ConfigureAwait(false);
            var id = reportId;
            ch.ReadRequested += async (_, args) => await HandleReadAsync(args, () => ReportFor(id, args.Session.DeviceId.Id)).ConfigureAwait(false);
            ch.SubscribedClientsChanged += (_, _) =>
            {
                OnSubscribersChanged(id);
                SubscriptionChanged?.Invoke(this, EventArgs.Empty);
            };

            var dres = await ch.CreateDescriptorAsync(ReportReference, new GattLocalDescriptorParameters
            {
                StaticValue = HidReports.InputReportReference(reportId).AsBuffer(),
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            }).AsTask(ct).ConfigureAwait(false);
            Check(dres.Error, $"report reference {reportId}");
            _reports[reportId] = ch;
        }
    }

    // What a read of a report returns to <paramref name="deviceId"/>: the last report for the input target, an idle
    // one for anyone else. Reading the keyboard or touch report over and over would otherwise show another connected
    // device what is typed on the target (security review, 2026-11-07). A device that is not known to be paired (also
    // when everyone gets the reports) reads an idle report as well, like it gets no notifications.
    private byte[] ReportFor(byte reportId, string deviceId) =>
        _pairing?.IsPaired(deviceId) == true && (!_targeted || string.Equals(deviceId, _target, StringComparison.Ordinal))
            ? LastReport(reportId)
            : IdleReport(reportId);

    private static byte[] IdleReport(byte reportId) => reportId switch
    {
        HidConstants.MouseReportId => HidReports.EncodeMouse(),
        HidConstants.ConsumerReportId => HidReports.EncodeConsumer(),
        HidConstants.KeyboardReportId => HidReports.EncodeKeyboard(),
        _ => HidReports.EncodeTouch([]),
    };

    private byte[] LastReport(byte reportId) => reportId switch
    {
        HidConstants.MouseReportId => _lastMouse,
        HidConstants.ConsumerReportId => _lastConsumer,
        HidConstants.KeyboardReportId => _lastKeyboard,
        _ => _lastTouch,
    };

    private void Note(string message)
    {
        _notes.Add(message);
        _log("note: " + message);
    }

    private async Task<GattServiceProvider> CreateProviderAsync(string name, Guid uuid, CancellationToken ct)
    {
        var result = await GattServiceProvider.CreateAsync(uuid).AsTask(ct).ConfigureAwait(false);
        Check(result.Error, $"create service {name} ({uuid})");
        var provider = result.ServiceProvider;
        provider.AdvertisementStatusChanged += (_, args) =>
        {
            _log($"service {name}: {args.Status}{(args.Error == BluetoothError.Success ? string.Empty : $" (error {args.Error})")}");
            AdvertisementStatusChanged?.Invoke(this, EventArgs.Empty);
        };
        _providers.Add((name, provider));
        return provider;
    }

    private static async Task CreateStaticAsync(GattServiceProvider provider, Guid uuid, GattCharacteristicProperties props, byte[] value, bool encrypted, CancellationToken ct)
    {
        var p = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = props,
            StaticValue = value.AsBuffer(),
        };
        if (encrypted)
        {
            p.ReadProtectionLevel = GattProtectionLevel.EncryptionRequired;
        }

        await CreateCharacteristicAsync(provider, uuid, p, $"characteristic {uuid}", ct).ConfigureAwait(false);
    }

    private static async Task<GattLocalCharacteristic> CreateCharacteristicAsync(GattServiceProvider provider, Guid uuid, GattLocalCharacteristicParameters p, string what, CancellationToken ct)
    {
        var result = await provider.Service.CreateCharacteristicAsync(uuid, p).AsTask(ct).ConfigureAwait(false);
        Check(result.Error, what);
        return result.Characteristic;
    }

    private static void Check(BluetoothError error, string what)
    {
        if (error != BluetoothError.Success)
        {
            throw new BleException($"{what}: BluetoothError={error}", error);
        }
    }

    private async Task HandleReadAsync(GattReadRequestedEventArgs args, Func<byte[]> getter)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            request?.RespondWithValue(getter().AsBuffer());
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            _log($"read request failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task HandleWriteAsync(GattWriteRequestedEventArgs args, Action<byte> store)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null)
            {
                return;
            }

            var data = Read(request.Value);
            if (data.Length > 0)
            {
                store(data[0]);
            }

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            _log($"write request failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static byte[] Read(IBuffer buffer) => buffer.Length == 0 ? [] : buffer.ToArray();
}

/// <summary>A Bluetooth call failed.</summary>
public sealed class BleException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BleException(string message, BluetoothError error)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Creates the exception.</summary>
    public BleException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public BleException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public BleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The Windows Bluetooth error code.</summary>
    public BluetoothError Error { get; }
}
