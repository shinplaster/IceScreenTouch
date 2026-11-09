// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Windows.Tests;

public class FirewallRulesTests
{
    private const string Exe = @"C:\apps\icescreentouch\IceScreenTouch.exe";
    private const int Public = 4;
    private const int Private = 2;

    private static RuleInfo Rule(
        string? app = Exe,
        int direction = 1,
        bool enabled = true,
        int action = 1,
        int protocol = 6,
        string? ports = "8000",
        string? remote = "LocalSubnet",
        int profiles = 0x7FFFFFFF) =>
        new(app, direction, enabled, action, protocol, ports, remote, profiles);

    private static InboundRuleScope Eval(params RuleInfo[] rules) => FirewallRules.Evaluate(rules, Exe, 8000, Public);

    [Fact]
    public void ASubnetRuleIsLimited() => Assert.Equal(InboundRuleScope.Limited, Eval(Rule()));

    [Fact]
    public void ProgramPathsCompareIgnoringCase() =>
        Assert.Equal(InboundRuleScope.Limited, Eval(Rule(app: Exe.ToLowerInvariant())));

    [Fact]
    public void TheAllowDialogsRuleIsBroadAndWinsOverASubnetRule() =>
        Assert.Equal(InboundRuleScope.Broad, Eval(Rule(), Rule(ports: "*", remote: "*", protocol: 256)));

    [Fact]
    public void ABlockRuleWins() =>
        Assert.Equal(InboundRuleScope.Blocked, Eval(Rule(ports: "*", remote: "*"), Rule(action: 0, ports: "*", remote: "*")));

    [Theory]
    [InlineData("other.exe", 1, true, 6, "8000", Public)]   // another program
    [InlineData(Exe, 2, true, 6, "8000", Public)]           // outbound
    [InlineData(Exe, 1, false, 6, "8000", Public)]          // disabled
    [InlineData(Exe, 1, true, 17, "8000", Public)]          // UDP
    [InlineData(Exe, 1, true, 6, "8001", Public)]           // another port
    [InlineData(Exe, 1, true, 6, "8000", Private)]          // only for the private profile, but the network is public
    public void RulesThatDoNotApplyAreIgnored(string app, int direction, bool enabled, int protocol, string ports, int profiles) =>
        Assert.Equal(InboundRuleScope.None, Eval(Rule(app, direction, enabled, protocol: protocol, ports: ports, profiles: profiles)));

    [Theory]
    [InlineData(null, true)]
    [InlineData("*", true)]
    [InlineData("8000", true)]
    [InlineData("80, 443,8000", true)]
    [InlineData("7000-9000", true)]
    [InlineData("8001-9000", false)]
    [InlineData("80,443", false)]
    [InlineData("RPC", false)]
    public void PortListsAndRanges(string? ports, bool covered) => Assert.Equal(covered, FirewallRules.CoversPort(ports, 8000));

    [Fact]
    public void TheEncodedCommandRunsTheScriptWithQuotedParameters()
    {
        var args = FirewallRules.EncodedCommandArguments("param([string]$Exe)\nWrite-Host $Exe", new Dictionary<string, string> { ["Exe"] = @"C:\it's here\a.exe" });

        Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", args, StringComparison.Ordinal);
        var command = Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ')[^1]));
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(@"C:\it's here\a.exe"));
        Assert.Equal($"& {{\nparam([string]$Exe)\nWrite-Host $Exe\n}} -Exe ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{value}')))", command);
    }

    [Theory]
    [InlineData(@"C:\a’ -Name (Write-Output INJECTED) #\a.exe")] // a typographic quote ends a '...' string in PowerShell
    [InlineData(@"C:\a‘$(Write-Output INJECTED)‛\b‚c\Bob’s\日本語\a.exe")]
    [InlineData("C:\\x' ; Write-Output INJECTED ; '\\a.exe")]
    public void AnyPathReachesTheScriptUnchanged(string exe)
    {
        var args = FirewallRules.EncodedCommandArguments(
            "param([string]$Exe)\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\nWrite-Output \"[$Exe]\"",
            new Dictionary<string, string> { ["Exe"] = exe });
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", args)
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();

        Assert.Equal($"[{exe}]", output);
    }

    [Fact]
    public void ParameterNamesMustBePlain() =>
        Assert.Throws<ArgumentException>(() => FirewallRules.EncodedCommandArguments("param($a)", new Dictionary<string, string> { ["a; calc"] = "x" }));

    [Fact]
    public void TheEncodedCommandReallyPassesTheParametersToPowerShell()
    {
        var args = FirewallRules.EncodedCommandArguments(
            "param([string]$Exe, [int]$Port = 1)\nWrite-Output \"$Exe|$Port\"",
            new Dictionary<string, string> { ["Exe"] = @"C:\it's here\a.exe", ["Port"] = "8000" });
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();

        Assert.Equal(@"C:\it's here\a.exe|8000", output);
    }

    [Fact]
    public void OnlyTheScriptOutputIsTakenFromARealTranscript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"icescreentouch-test-{Guid.NewGuid():N}.log");
        try
        {
            var args = FirewallRules.EncodedCommandArguments(
                "param([string]$LogPath)\nStart-Transcript -Path $LogPath -Force | Out-Null\nWrite-Host 'target: a.exe'\nWrite-Host 'DONE'\nStop-Transcript | Out-Null",
                new Dictionary<string, string> { ["LogPath"] = path });
            using (var p = Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true })!)
            {
                p.WaitForExit();
            }

            var lines = File.ReadAllLines(path);

            Assert.Contains(lines, l => l.Contains("EncodedCommand", StringComparison.Ordinal)); // the header repeats the command
            Assert.Equal(["target: a.exe", "DONE"], FirewallRules.TranscriptBody(lines));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheRulesCanBeReadWithoutAdministratorRights()
    {
        // Whatever the machine has, reading must not throw (the app shows "unknown" if it does).
        var scope = FirewallRules.InboundTcp(Environment.ProcessPath!, 8000);

        Assert.True(Enum.IsDefined(scope));
    }

    [Fact]
    public void TheSourceGeneratedReaderMatchesLateBoundCom()
    {
        // The app reads the rules with source-generated COM (trim safe). Late-bound COM (what the app used before
        // the trimmed build) is the reference: the vtable slots must line up with netfw.h.
        var (profiles, rules) = FirewallRules.ReadRules();
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!;
        dynamic policy = Activator.CreateInstance(type)!;
        var expected = new List<RuleInfo>();
        foreach (dynamic rule in policy.Rules)
        {
            expected.Add(new RuleInfo(
                (string?)rule.ApplicationName, (int)rule.Direction, (bool)rule.Enabled, (int)rule.Action,
                (int)rule.Protocol, (string?)rule.LocalPorts, (string?)rule.RemoteAddresses, (int)rule.Profiles));
        }

        Assert.Equal((int)policy.CurrentProfileTypes, profiles);
        Assert.NotEmpty(rules);
        Assert.Equal(expected, rules);
    }

    [Theory]
    [InlineData(InboundRuleScope.Limited, false, FirewallJudgement.Allowed)]
    [InlineData(InboundRuleScope.Broad, false, FirewallJudgement.TooBroad)]
    [InlineData(InboundRuleScope.None, false, FirewallJudgement.Missing)]
    [InlineData(InboundRuleScope.Blocked, false, FirewallJudgement.Missing)]
    [InlineData(InboundRuleScope.Limited, true, FirewallJudgement.AllowedByPackage)]
    [InlineData(InboundRuleScope.Broad, true, FirewallJudgement.AllowedByPackage)]
    [InlineData(InboundRuleScope.None, true, FirewallJudgement.Missing)]
    [InlineData(InboundRuleScope.Blocked, true, FirewallJudgement.Missing)]
    public void TheJudgementDependsOnWhetherThePackageOwnsTheRule(InboundRuleScope scope, bool packaged, FirewallJudgement expected) =>
        Assert.Equal(expected, FirewallRules.Judge(scope, packaged));

    [Fact]
    public void AManifestRuleForTheInstalledExeIsAnyAddressAndIsAcceptedWhenPackaged()
    {
        // The rule the manifest creates: every profile, TCP 8000, any remote address, program in the WindowsApps folder.
        const string installed = @"C:\Program Files\WindowsApps\IceScreenTouch_0.1.0.0_x64__abc123\IceScreenTouch.exe";
        var scope = FirewallRules.Evaluate([Rule(app: installed.ToUpperInvariant(), remote: "*")], installed, 8000, Public);

        Assert.Equal(InboundRuleScope.Broad, scope);
        Assert.Equal(FirewallJudgement.AllowedByPackage, FirewallRules.Judge(scope, packaged: true));
    }
}
