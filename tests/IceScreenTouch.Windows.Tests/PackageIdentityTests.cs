// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Platform.Windows;
using Windows.ApplicationModel;

namespace IceScreenTouch.Windows.Tests;

public class PackageIdentityTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(122, true)]      // ERROR_INSUFFICIENT_BUFFER: the empty buffer is too small for the name
    [InlineData(15700, false)]   // APPMODEL_ERROR_NO_PACKAGE
    [InlineData(87, false)]      // any other error
    public void OnlyASuccessOrATooSmallBufferMeansPackaged(int result, bool packaged) =>
        Assert.Equal(packaged, PackageIdentity.IndicatesPackage(result));

    [Fact]
    public void OnlyAStoreSignedPackageChecksTheStoreLicense()
    {
        Assert.True(PackageIdentity.IsStoreSigned(PackageSignatureKind.Store));
        Assert.False(PackageIdentity.IsStoreSigned(PackageSignatureKind.Developer));
        Assert.False(PackageIdentity.IsStoreSigned(PackageSignatureKind.Enterprise));
        Assert.False(PackageIdentity.IsStoreSigned(PackageSignatureKind.None));
        Assert.False(PackageIdentity.IsStoreSigned(PackageSignatureKind.System));
        Assert.False(PackageIdentity.IsStoreSigned(null)); // not packaged, or unreadable
    }

    [Fact]
    public void ThePlainTestHostHasNoSignatureAndNoLicenseCheck()
    {
        Assert.Null(PackageIdentity.SignatureKind);
        Assert.False(PackageIdentity.UsesStoreLicense);
    }

    [Fact]
    public void TheTestHostHasNoPackageIdentity()
    {
        Assert.False(PackageIdentity.IsPackaged);
        Assert.Null(PackageIdentity.FullName);
        Assert.Null(PackageIdentity.FamilyName);
    }

    [Fact]
    public void IsPackagedFollowsTheFullName()
    {
        // Guards the static initializer order bug: IsPackaged once read FullName before it was initialized.
        Assert.Equal(PackageIdentity.FullName is not null, PackageIdentity.IsPackaged);
        Assert.Equal(PackageIdentity.FullName is not null, PackageIdentity.FamilyName is not null);
    }
}

public class PackagePathsTests
{
    private const string Local = @"C:\Users\me\AppData\Local";
    private const string Family = "Pub.IceScreenTouch_abc123";
    private const string Redirected = @"C:\Users\me\AppData\Local\Packages\Pub.IceScreenTouch_abc123\LocalCache\Local";

    [Fact]
    public void ARedirectedFolderIsOpenedInPlaceOfTheOriginal()
    {
        var result = PackagePaths.ForExplorer($@"{Local}\IceScreenTouch\logs", Local, Family, p => p == $@"{Redirected}\IceScreenTouch\logs");
        Assert.Equal($@"{Redirected}\IceScreenTouch\logs", result);
    }

    [Fact]
    public void WithoutARedirectedFolderTheOriginalIsKept()
    {
        var path = $@"{Local}\IceScreenTouch";
        Assert.Equal(path, PackagePaths.ForExplorer(path, Local, Family, _ => false));
    }

    [Fact]
    public void NotPackagedKeepsThePathWithoutLookingAtTheFileSystem()
    {
        var path = $@"{Local}\IceScreenTouch";
        Assert.Equal(path, PackagePaths.ForExplorer(path, Local, null, _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(@"D:\data\IceScreenTouch")]
    [InlineData(@"C:\Users\me\AppData\LocalLow\IceScreenTouch")]   // shares the prefix but is another folder
    [InlineData(@"C:\Users\me\AppData\Local")]                      // the root itself is not redirected
    public void PathsOutsideLocalAppDataAreKept(string path) =>
        Assert.Equal(path, PackagePaths.ForExplorer(path, Local, Family, _ => true));

    [Fact]
    public void CaseAndATrailingSeparatorDoNotMatter()
    {
        var result = PackagePaths.ForExplorer(@"c:\users\ME\appdata\local\IceScreenTouch\", Local + @"\", Family, _ => true);
        Assert.Equal($@"{Redirected}\IceScreenTouch", result);
    }
}
