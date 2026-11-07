using System.Globalization;
using System.Text;

namespace IceScreenTouch.Platform.Windows;

/// <summary>How the Windows Firewall treats inbound TCP connections to a program's port.</summary>
public enum InboundRuleScope
{
    /// <summary>No enabled rule for the current profile mentions the program and port.</summary>
    None,

    /// <summary>Allowed from the local subnet (or from listed addresses) only.</summary>
    Limited,

    /// <summary>Allowed from any address.</summary>
    Broad,

    /// <summary>A block rule applies (it wins over allow rules).</summary>
    Blocked,
}

/// <summary>What the probe tab concludes from the firewall rules for the probe page port.</summary>
public enum FirewallJudgement
{
    /// <summary>A rule allows the port from the local subnet only (the rule of the "set up the firewall" button).</summary>
    Allowed,

    /// <summary>No enabled allow rule applies, or a block rule wins.</summary>
    Missing,

    /// <summary>The port is allowed from anywhere (a plain executable: works, but broader than needed).</summary>
    TooBroad,

    /// <summary>The package's own rule allows the port (an installed MSIX package: its manifest rule has no address limit).</summary>
    AllowedByPackage,
}

/// <summary>
/// Reads the Windows Firewall rules through the HNetCfg.FwPolicy2 COM object (reading needs no administrator
/// rights; changing them does, see the allow_firewall.ps1 script).
/// </summary>
public static class FirewallRules
{
    private const int DirectionIn = 1;      // NET_FW_RULE_DIR_IN
    private const int ActionAllow = 1;      // NET_FW_ACTION_ALLOW
    private const int ProtocolTcp = 6;      // NET_FW_IP_PROTOCOL_TCP
    private const int ProtocolAny = 256;    // NET_FW_IP_PROTOCOL_ANY

    /// <summary>
    /// The effective scope of the enabled inbound rules of the current profiles that name
    /// <paramref name="programPath"/> and cover TCP <paramref name="port"/>.
    /// </summary>
    public static InboundRuleScope InboundTcp(string programPath, int port)
    {
        ArgumentException.ThrowIfNullOrEmpty(programPath);
        var (profiles, rules) = ReadRules();
        return Evaluate(rules, programPath, port, profiles);
    }

    /// <summary>
    /// What the rules mean for the probe page. A plain executable should have a rule limited to the local subnet
    /// (a rule for any address is "too broad"). An installed package gets its rule from the manifest, which cannot
    /// limit the address, so any allow rule is fine there (a block rule is still a fault).
    /// </summary>
    public static FirewallJudgement Judge(InboundRuleScope scope, bool packaged) => scope switch
    {
        InboundRuleScope.Limited or InboundRuleScope.Broad when packaged => FirewallJudgement.AllowedByPackage,
        InboundRuleScope.Limited => FirewallJudgement.Allowed,
        InboundRuleScope.Broad => FirewallJudgement.TooBroad,
        _ => FirewallJudgement.Missing,
    };

    /// <summary>The current profile types (NET_FW_PROFILE_TYPE2 bits) and every rule of the local policy.</summary>
    public static (int Profiles, IReadOnlyList<RuleInfo> Rules) ReadRules() => FirewallCom.Read();

    /// <summary>The decision of <see cref="InboundTcp"/> over already read rules (testable without COM).</summary>
    public static InboundRuleScope Evaluate(IEnumerable<RuleInfo> rules, string programPath, int port, int currentProfiles)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var result = InboundRuleScope.None;
        foreach (var r in rules)
        {
            if (r.Direction != DirectionIn || !r.Enabled || (r.Profiles & currentProfiles) == 0
                || !string.Equals(r.ApplicationName, programPath, StringComparison.OrdinalIgnoreCase)
                || r.Protocol is not (ProtocolTcp or ProtocolAny) || !CoversPort(r.LocalPorts, port))
            {
                continue;
            }

            if (r.Action != ActionAllow)
            {
                return InboundRuleScope.Blocked;
            }

            var broad = string.IsNullOrEmpty(r.RemoteAddresses) || r.RemoteAddresses == "*";
            var scope = broad ? InboundRuleScope.Broad : InboundRuleScope.Limited;
            if (scope > result)
            {
                result = scope;
            }
        }

        return result;
    }

    /// <summary>Whether a LocalPorts value ("*", "8000", "80,443", "8000-8010") covers <paramref name="port"/>.</summary>
    public static bool CoversPort(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts) || localPorts.Trim() == "*")
        {
            return true;
        }

        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0
                && int.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var lo)
                && int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var hi))
            {
                if (port >= lo && port <= hi)
                {
                    return true;
                }
            }
            else if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The script's own output from a PowerShell transcript: the lines between the header block and the
    /// footer block (each block is framed by lines of asterisks; the header repeats the whole encoded command).
    /// </summary>
    public static IEnumerable<string> TranscriptBody(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var separators = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("*****", StringComparison.Ordinal))
            {
                separators++;
                continue;
            }

            if (separators == 2 && !string.IsNullOrWhiteSpace(line))
            {
                yield return line.TrimEnd();
            }
        }
    }

    /// <summary>
    /// The command line arguments that run <paramref name="script"/> (the text of a PowerShell script with a
    /// param block) with the given named parameters, without a script file: a file that a non-elevated process
    /// could change between the check and the elevation is avoided. Each value goes in as Base64 and is decoded by
    /// PowerShell: the values come from outside (the exe path, TEMP), and PowerShell also takes the typographic
    /// quotes ‘ ’ ‚ ‛ as single quotes, so quoting them would let a folder name end the string and run code as
    /// administrator (security review, 2026-11-07). Parameter names must be plain identifiers.
    /// </summary>
    public static string EncodedCommandArguments(string script, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(parameters);
        var command = new StringBuilder("& {\n").Append(script).Append("\n}");
        foreach (var (name, value) in parameters)
        {
            if (name.Length == 0 || !name.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException($"not a parameter name: {name}", nameof(parameters));
            }

            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            command.Append(" -").Append(name).Append(" ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('")
                .Append(base64).Append("')))");
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command.ToString()));
        return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
    }
}

/// <summary>The parts of a firewall rule that <see cref="FirewallRules"/> looks at.</summary>
public sealed record RuleInfo(
    string? ApplicationName,
    int Direction,
    bool Enabled,
    int Action,
    int Protocol,
    string? LocalPorts,
    string? RemoteAddresses,
    int Profiles);
