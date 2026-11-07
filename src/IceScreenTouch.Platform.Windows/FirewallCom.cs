using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Reads the Windows Firewall rules through the HNetCfg.FwPolicy2 COM object with source-generated COM
/// interop, so it keeps working in a trimmed single-file build (late-bound <c>dynamic</c> COM does not).
/// Only the vtable slots that are called have real signatures; the others are placeholders that keep the
/// slot order of netfw.h.
/// </summary>
internal static unsafe partial class FirewallCom
{
    private static readonly Guid ClsidFwPolicy2 = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private const uint ClsctxInprocServer = 0x1;
    private const ushort VtDispatch = 9;
    private const ushort VtUnknown = 13;

    /// <summary>The current profile types and every rule of the local policy.</summary>
    public static (int Profiles, IReadOnlyList<RuleInfo> Rules) Read()
    {
        var iid = typeof(INetFwPolicy2).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidFwPolicy2, 0, ClsctxInprocServer, iid, out var unknown));
        INetFwPolicy2 policy;
        try
        {
            policy = (INetFwPolicy2)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        var rules = new List<RuleInfo>();
        try
        {
            var profiles = policy.GetCurrentProfileTypes();
            var collection = policy.GetRules();
            var enumUnknown = collection.GetNewEnum();
            IEnumVariant enumerator;
            try
            {
                enumerator = (IEnumVariant)Wrappers.GetOrCreateObjectForComInstance(enumUnknown, CreateObjectFlags.UniqueInstance);
            }
            finally
            {
                Marshal.Release(enumUnknown);
            }

            Variant item;
            while (enumerator.Next(1, &item, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (item.Type is VtDispatch or VtUnknown && item.Pointer != 0)
                    {
                        var rule = (INetFwRule)Wrappers.GetOrCreateObjectForComInstance(item.Pointer, CreateObjectFlags.UniqueInstance);
                        rules.Add(new RuleInfo(
                            rule.GetApplicationName(),
                            rule.GetDirection(),
                            rule.GetEnabled(),
                            rule.GetAction(),
                            rule.GetProtocol(),
                            rule.GetLocalPorts(),
                            rule.GetRemoteAddresses(),
                            ReadProfiles(rule)));
                        ((ComObject)(object)rule).FinalRelease();
                    }
                }
                finally
                {
                    _ = VariantClear(&item);
                }
            }

            ((ComObject)(object)enumerator).FinalRelease();
            ((ComObject)(object)collection).FinalRelease();
            return (profiles, rules);
        }
        finally
        {
            ((ComObject)(object)policy).FinalRelease();
        }
    }


    private static int ReadProfiles(INetFwRule rule)
    {
        var profiles = 0;
        Marshal.ThrowExceptionForHR(rule.GetProfiles(&profiles));
        return profiles;
    }
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint unknown);

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(Variant* variant);

    /// <summary>VARIANT (24 bytes on x64); only the type and the interface pointer are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Variant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Pointer;
        public nint Extra;
    }

    // IDispatch occupies four slots after IUnknown; the firewall interfaces derive from it.

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
    [Guid("98325047-C671-4174-8D81-DEFCD3F03186")]
    internal partial interface INetFwPolicy2
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();

        int GetCurrentProfileTypes();
        void GetFirewallEnabled();
        void PutFirewallEnabled();
        void GetExcludedInterfaces();
        void PutExcludedInterfaces();
        void GetBlockAllInboundTraffic();
        void PutBlockAllInboundTraffic();
        void GetNotificationsDisabled();
        void PutNotificationsDisabled();
        void GetUnicastResponsesToMulticastBroadcastDisabled();
        void PutUnicastResponsesToMulticastBroadcastDisabled();
        INetFwRules GetRules();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
    [Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009")]
    internal partial interface INetFwRules
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();

        int GetCount();
        void Add();
        void Remove();
        void Item();
        nint GetNewEnum();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
    [Guid("AF230D27-BABA-4E42-ACED-F524F22CFCE2")]
    internal partial interface INetFwRule
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();

        string? GetName();
        void PutName();
        string? GetDescription();
        void PutDescription();
        string? GetApplicationName();
        void PutApplicationName();
        string? GetServiceName();
        void PutServiceName();
        int GetProtocol();
        void PutProtocol();
        string? GetLocalPorts();
        void PutLocalPorts();
        string? GetRemotePorts();
        void PutRemotePorts();
        string? GetLocalAddresses();
        void PutLocalAddresses();
        string? GetRemoteAddresses();
        void PutRemoteAddresses();
        string? GetIcmpTypesAndCodes();
        void PutIcmpTypesAndCodes();
        int GetDirection();
        void PutDirection();
        void GetInterfaces();
        void PutInterfaces();
        string? GetInterfaceTypes();
        void PutInterfaceTypes();
        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetEnabled();
        void PutEnabled();
        string? GetGrouping();
        void PutGrouping();
        // The implementation ORs the profile bits into the caller's variable, so it must start at zero.
        [PreserveSig]
        int GetProfiles(int* profiles);
        void PutProfiles();
        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetEdgeTraversal();
        void PutEdgeTraversal();
        int GetAction();
    }

    [GeneratedComInterface]
    [Guid("00020404-0000-0000-C000-000000000046")]
    internal partial interface IEnumVariant
    {
        [PreserveSig]
        int Next(uint count, Variant* items, out uint fetched);
    }
}
