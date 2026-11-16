// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace IceScreenTouch.Api;

/// <summary>
/// The Medium mandatory integrity label (No-Read-Up, No-Write-Up) of the secret files of the API (<c>api.json</c>, the token file).
/// The DACL already keeps other accounts out, but a process of the SAME user that runs at Low integrity (a sandboxed browser
/// renderer, a downloaded program started in a low-integrity container) passes a DACL that names the user: the label is what
/// keeps it from reading the token. Setting a label needs WRITE_OWNER on the file (the creator has it), not SeSecurityPrivilege,
/// which is why it goes through <c>SetSecurityInfo</c> / <c>SetNamedSecurityInfo</c> with <c>LABEL_SECURITY_INFORMATION</c> and not
/// through <see cref="System.Security.AccessControl.FileSecurity"/> (which would ask for the privilege for any SACL).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class IntegrityLabel
{
    /// <summary>The label: a mandatory label ACE (ML) with No-Write-Up and No-Read-Up for the Medium level (ME).</summary>
    internal const string Sddl = "S:(ML;;NRNW;;;ME)";

    private const int SeFileObject = 1;
    private const uint LabelSecurityInformation = 0x00000010;
    private const uint SddlRevision1 = 1;

    /// <summary>Puts the label on an open file (the handle needs WRITE_OWNER). Null when done, otherwise what went wrong.</summary>
    internal static string? TrySet(SafeFileHandle file) => TrySet(Sddl, sacl => SetSecurityInfo(file.DangerousGetHandle(), SeFileObject, LabelSecurityInformation, 0, 0, 0, sacl));

    /// <summary>Puts the label on the file at <paramref name="path"/> (not open with a sharing mode that excludes it). Null when done, otherwise what went wrong.</summary>
    internal static string? TrySet(string path) => TrySet(path, Sddl);

    /// <summary>Puts the label given as SDDL on the file at <paramref name="path"/> (the tests use it to make a label that is not the expected one).</summary>
    internal static string? TrySet(string path, string sddl) => TrySet(sddl, sacl => SetNamedSecurityInfo(path, SeFileObject, LabelSecurityInformation, 0, 0, 0, sacl));

    /// <summary>
    /// True when <paramref name="sddl"/> (as <see cref="Read"/> returns it, for example <c>S:AI(ML;;NWNR;;;ME)</c>) holds exactly one
    /// mandatory label ACE for the Medium level with No-Read-Up and No-Write-Up and nothing else: the order of the two rights and the
    /// inheritance flags of the SACL (<c>AI</c>, <c>AR</c>, <c>P</c>) do not matter. An empty string (no label) is not.
    /// </summary>
    internal static bool IsExpected(string sddl)
    {
        // Only a SACL that is nothing but the label counts: S:<flags>(ML;;<rights>;;;ME)
        var match = LabelPattern().Match(sddl);
        if (!match.Success)
        {
            return false;
        }

        var rights = match.Groups["rights"].Value;
        return rights.Length == 4 && rights.Contains("NR", StringComparison.Ordinal) && rights.Contains("NW", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^S:(?:P|AI|AR|NO_ACCESS_CONTROL)*\(ML;(?:[A-Z]{0,6});(?<rights>[A-Z]*);;;(?:ME|0x2000|MEDIUM)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    /// <summary>The label of the file as SDDL (<c>S:(ML;;NWNR;;;ME)</c>), an empty string when the file has none, null when it cannot be read.</summary>
    internal static string? Read(string path)
    {
        if (GetNamedSecurityInfo(path, SeFileObject, LabelSecurityInformation, 0, 0, 0, out var sacl, out var descriptor) != 0)
        {
            return null;
        }

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, SddlRevision1, LabelSecurityInformation, out var text, 0))
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(text) ?? string.Empty;
            }
            finally
            {
                _ = LocalFree(text);
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
            _ = sacl; // points into the descriptor
        }
    }

    // Builds the SACL from the SDDL and hands it to the call that applies it (null = ok, else the message).
    private static string? TrySet(string sddl, Func<nint, uint> apply)
    {
        try
        {
            return TrySetCore(sddl, apply);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ex.GetType().Name;
        }
    }

    private static string? TrySetCore(string sddl, Func<nint, uint> apply)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out var descriptor, 0))
        {
            return Message("building the label", Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present || sacl == 0)
            {
                return Message("reading the label", Marshal.GetLastPInvokeError());
            }

            var error = apply(sacl);
            return error == 0 ? null : Message("setting the label", (int)error);
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static string Message(string what, int error) => $"{what} failed ({new Win32Exception(error).Message})";

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint descriptor, nint size);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSecurityDescriptorToStringSecurityDescriptor(nint descriptor, uint revision, uint information, out nint text, nint length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorSacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(nint handle, int objectType, uint information, nint owner, nint group, nint dacl, nint sacl);

    [LibraryImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint SetNamedSecurityInfo(string path, int objectType, uint information, nint owner, nint group, nint dacl, nint sacl);

    [LibraryImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetNamedSecurityInfo(string path, int objectType, uint information, nint owner, nint group, nint dacl, out nint sacl, out nint descriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
