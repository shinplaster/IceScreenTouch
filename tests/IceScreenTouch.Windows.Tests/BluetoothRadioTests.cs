// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Windows.Tests;

public class BluetoothRadioTests
{
    [Fact]
    public void TheRadioNameIsReadWithoutThrowingAndIsTrimmed()
    {
        // A PC without a Bluetooth radio (a CI runner) gives null; one with a radio gives its name, which Windows takes
        // from the device name (compared without case: the NetBIOS name is upper case, the radio's keeps the case).
        var name = BluetoothRadio.ReadName();

        if (name is not null)
        {
            Assert.Equal(name.Trim(), name);
            Assert.NotEmpty(name);
            Assert.DoesNotContain('\0', name);
        }
    }
}
