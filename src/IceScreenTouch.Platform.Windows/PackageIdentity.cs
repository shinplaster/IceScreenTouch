using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Whether the process runs with a package identity, i.e. installed from an MSIX package (the Microsoft Store
/// build) rather than started as a plain executable (a build of your own).
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
    /// How the package is signed (<c>Package.Current.SignatureKind</c>): <see cref="PackageSignatureKind.Store"/> for the
    /// package that came from the Microsoft Store, Developer / Enterprise / System / None for a local test package.
    /// Null when not packaged or when it cannot be read.
    /// </summary>
    public static PackageSignatureKind? SignatureKind { get; } = ReadSignatureKind();

    /// <summary>
    /// Only the package that came from the Microsoft Store has a Store license to check. A test package signed locally
    /// (Developer, Enterprise, None, System), a plain executable and an unreadable signature all behave like a build
    /// of your own: no license check.
    /// </summary>
    public static bool UsesStoreLicense => IsStoreSigned(SignatureKind);

    /// <summary>The decision behind <see cref="UsesStoreLicense"/>.</summary>
    internal static bool IsStoreSigned(PackageSignatureKind? kind) => kind == PackageSignatureKind.Store;

    private static PackageSignatureKind? ReadSignatureKind()
    {
        try
        {
            return FullName is null ? null : Package.Current.SignatureKind;
        }
#pragma warning disable CA1031 // Any failure means "not the Store package": no license check.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

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
