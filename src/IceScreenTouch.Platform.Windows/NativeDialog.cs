using System.Runtime.InteropServices;

namespace IceScreenTouch.Platform.Windows;

/// <summary>A plain Win32 message box, for messages before (or without) any UI framework.</summary>
public static partial class NativeDialog
{
    private const uint MbIconInformation = 0x40;
    private const uint MbIconError = 0x10;

    /// <summary>Shows an information message and waits for OK.</summary>
    public static void Info(string title, string text) => _ = MessageBox(0, text, title, MbIconInformation);

    /// <summary>Shows an error message and waits for OK.</summary>
    public static void Error(string title, string text) => _ = MessageBox(0, text, title, MbIconError);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
