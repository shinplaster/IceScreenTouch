using System.Runtime.InteropServices;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Whether the process runs with a package identity, i.e. installed from an MSIX package (the Microsoft Store
/// build) rather than started as a plain executable (the GitHub build).
/// </summary>
public static partial class PackageIdentity
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>
    /// True when the process has a package identity. Derived from <see cref="FullName"/> on every read: static
    /// initializers run in textual order, so a property initialized from it above its declaration would be always false.
    /// </summary>
    public static bool IsPackaged => FullName is not null;

    /// <summary>The package full name (<c>Name_Version_Architecture_ResourceId_PublisherId</c>), or null when not packaged.</summary>
    public static string? FullName { get; } = ReadFullName();

    /// <summary>The package family name (<c>Name_PublisherId</c>), or null when not packaged.</summary>
    public static string? FamilyName { get; } = ReadFamilyName();

    /// <summary>
    /// Whether the result of <c>GetCurrentPackageFullName</c> called with an empty buffer says "packaged":
    /// ERROR_INSUFFICIENT_BUFFER (the name does not fit) or success. APPMODEL_ERROR_NO_PACKAGE (15700) and any
    /// other error mean no identity.
    /// </summary>
    internal static bool IndicatesPackage(int result) => result is ErrorSuccess or ErrorInsufficientBuffer;

    private static unsafe string? ReadFullName()
    {
        try
        {
            uint length = 0;
            var probe = GetCurrentPackageFullName(ref length, null);
            if (!IndicatesPackage(probe) || length == 0)
            {
                return null;
            }

            var buffer = new char[length];
            fixed (char* name = buffer)
            {
                return GetCurrentPackageFullName(ref length, name) == ErrorSuccess && length > 1
                    ? new string(buffer, 0, (int)length - 1)
                    : null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows older than 8 has no package model; nothing there is packaged.
            return null;
        }
    }

    private static unsafe string? ReadFamilyName()
    {
        try
        {
            uint length = 0;
            var probe = GetCurrentPackageFamilyName(ref length, null);
            if (!IndicatesPackage(probe) || length == 0)
            {
                return null;
            }

            var buffer = new char[length];
            fixed (char* name = buffer)
            {
                return GetCurrentPackageFamilyName(ref length, name) == ErrorSuccess && length > 1
                    ? new string(buffer, 0, (int)length - 1)
                    : null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, char* packageFamilyName);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(ref uint packageFullNameLength, char* packageFullName);
}
