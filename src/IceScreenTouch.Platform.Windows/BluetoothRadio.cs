// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// The local Bluetooth radio's name: what other devices see in their Bluetooth list ("discoverable as" in the Windows
/// settings). It keeps the case of the Windows device name, unlike <see cref="Environment.MachineName"/> (the NetBIOS name,
/// upper case).
/// </summary>
public static partial class BluetoothRadio
{
    private const int MaxNameSize = 248;
    private const uint ErrorSuccess = 0;

    /// <summary>The first radio's name, or null when there is no radio or it cannot be read.</summary>
    public static unsafe string? ReadName()
    {
        try
        {
            var find = new FindRadioParams { Size = (uint)sizeof(FindRadioParams) };
            var search = BluetoothFindFirstRadio(ref find, out var radio);
            if (search == 0)
            {
                return null;
            }

            try
            {
                var info = new RadioInfo { Size = (uint)sizeof(RadioInfo) };
                if (BluetoothGetRadioInfo(radio, ref info) != ErrorSuccess)
                {
                    return null;
                }

                var name = new string((char*)info.Name, 0, MaxNameSize);
                var end = name.IndexOf('\0', StringComparison.Ordinal);
                name = (end >= 0 ? name[..end] : name).Trim();
                return name.Length > 0 ? name : null;
            }
            finally
            {
                _ = CloseHandle(radio);
                _ = BluetoothFindRadioClose(search);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No Bluetooth stack on this Windows: the caller falls back to the host name.
            return null;
        }
    }

    // BLUETOOTH_FIND_RADIO_PARAMS
    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams
    {
        public uint Size;
    }

    // BLUETOOTH_RADIO_INFO (520 bytes)
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RadioInfo
    {
        public uint Size;
        public ulong Address;
        public fixed ushort Name[MaxNameSize]; // WCHAR
        public uint ClassOfDevice;
        public ushort LmpSubversion;
        public ushort Manufacturer;
    }

    [LibraryImport("BluetoothApis.dll")]
    private static partial nint BluetoothFindFirstRadio(ref FindRadioParams parameters, out nint radio);

    [LibraryImport("BluetoothApis.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindRadioClose(nint find);

    [LibraryImport("BluetoothApis.dll")]
    private static partial uint BluetoothGetRadioInfo(nint radio, ref RadioInfo info);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
