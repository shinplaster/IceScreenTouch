using System.Diagnostics;
using System.Globalization;
using System.Net;
using IceScreenTouch.App;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Ble;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using IceScreenTouch.Probe;

namespace IceScreenTouch.Windows;

/// <summary>
/// <c>IceScreenTouch.exe --self-check</c>: exercises the parts that a trimmed single-file build can break
/// (JSON source generation, source-generated COM, the WinRT projection, Kestrel, P/Invoke) without a window,
/// without advertising and without touching the user's files. Prints one line per check; exit code 0 = all
/// passed. Used by tools/publish.ps1 on the published executable.
/// </summary>
internal static class SelfCheck
{
    public const string Argument = "--self-check";

    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;

        async Task Check(string name, Func<Task<string>> check)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var detail = await check().ConfigureAwait(false);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PASS {name} ({watch.ElapsedMilliseconds} ms): {detail}"));
            }
#pragma warning disable CA1031 // every failure is reported, none may stop the other checks
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures++;
                output.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Check("package", () =>
        {
            // Packaged (Microsoft Store / MSIX build) or a plain executable (a build of your own); either is fine here, the
            // firewall check below judges the rule accordingly.
            return Task.FromResult(PackageIdentity.IsPackaged ? $"packaged: {PackageIdentity.FullName}" : "not packaged (plain executable)");
        }).ConfigureAwait(false);

        await Check("settings", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"icescreentouch-selfcheck-{Guid.NewGuid():N}");
            try
            {
                var store = new SettingsStore(Path.Combine(dir, "settings.json"));
                var saved = new AppSettings { AirPlayName = "check", LandscapeRotation = 270, WheelStepPercent = 7, Receiver = ReceiverMode.AttachToProcess };
                store.Save(saved);
                var loaded = store.Load(out var problem);
                Expect(problem is null, $"load problem: {problem}");
                Expect(loaded == saved, "the settings read back differ from the ones written");
                return Task.FromResult("written and read back");
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }).ConfigureAwait(false);

        await Check("strings", () =>
        {
            // The neutral text is in the main assembly, every other language in a satellite assembly of the bundle.
            var en = Strings.ResourceManager.GetString(nameof(Strings.TabStatus), CultureInfo.InvariantCulture);
            var found = new List<string>();
            foreach (var code in AppLanguage.Supported.Where(c => c != "en"))
            {
                var culture = CultureInfo.GetCultureInfo(code);
                var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
                var text = set?.GetString(nameof(Strings.TabStatus));
                Expect(!string.IsNullOrEmpty(text) && text != en, $"no '{code}' text (satellite assembly missing?)");
                found.Add($"{code} \"{text}\"");
            }

            Expect(!string.IsNullOrEmpty(en), "no neutral (English) text");
            return Task.FromResult($"en \"{en}\", {string.Join(", ", found)}; UI language {CultureInfo.CurrentUICulture.Name}");
        }).ConfigureAwait(false);

        await Check("about", () =>
        {
            // The About tab: the license and the notices embedded in the bundle (the version is only shown: a build
            // from a source zip has no commit, and that is not a fault).
            Expect(AppInfo.LicenseText.Contains("MIT License", StringComparison.Ordinal), "no license text");
            var components = AppInfo.Components;
            Expect(components.Count > 0, "no third-party components in the notices");
            return Task.FromResult($"{AppInfo.InformationalVersion}; {components.Count} components, notices {AppInfo.NoticesText.Length / 1024} KB");
        }).ConfigureAwait(false);

        await Check("firewall", () =>
        {
            var (profiles, rules) = FirewallRules.ReadRules();
            Expect(rules.Count > 0, "no rules read");
            Expect(rules.Any(r => !string.IsNullOrEmpty(r.ApplicationName)), "no rule has a program path");
            var scope = FirewallRules.InboundTcp(Environment.ProcessPath!, WindowsProbeService.DefaultPort);
            var packaged = PackageIdentity.IsPackaged;
            var judgement = FirewallRules.Judge(scope, packaged);
            var line = string.Create(CultureInfo.InvariantCulture, $"{rules.Count} rules, profiles 0x{profiles:X}, this exe on TCP {WindowsProbeService.DefaultPort}: {scope}, judged {judgement}");
            // An installed package must have the manifest's rule; a plain executable may have none (the button creates it).
            Expect(!packaged || judgement == FirewallJudgement.AllowedByPackage, $"packaged, but the manifest's firewall rule is not in effect: {line}");
            return Task.FromResult(line);
        }).ConfigureAwait(false);

        await Check("bluetooth", async () =>
        {
            var info = await BleAdapterInfo.GetDefaultAsync().ConfigureAwait(false);
            return info.Describe();
        }).ConfigureAwait(false);

        await Check("probe server", async () =>
        {
            await using var server = new ProbeServer(0, IPAddress.Loopback);
            await server.StartAsync().ConfigureAwait(false);
            using var http = new HttpClient { BaseAddress = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{server.Port}")) };
            var page = await http.GetStringAsync(new Uri("/touch", UriKind.Relative)).ConfigureAwait(false);
            var scroll = await http.GetStringAsync(new Uri("/scroll", UriKind.Relative)).ConfigureAwait(false);
            Expect(page == ProbeServer.Page && scroll == ProbeServer.ScrollPage, "the pages differ from the embedded ones");
            using var body = new StringContent("""{"type":"pointerdown","id":1,"ptype":"touch","nx":5000,"ny":2500,"seq":1}""");
            using (var refused = await http.PostAsync(new Uri("/e", UriKind.Relative), body).ConfigureAwait(false))
            {
                Expect(refused.StatusCode == HttpStatusCode.Forbidden, $"an event without the run's key got {(int)refused.StatusCode}, not 403");
            }

            using var response = await http.PostAsync(new Uri($"/e?k={server.Key}", UriKind.Relative), body).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var events = server.Snapshot();
            Expect(events is [{ Type: "pointerdown", Nx: 5000, Ny: 2500 }], "the posted event was not recorded");
            return "pages served, event recorded";
        }).ConfigureAwait(false);

        await Check("picture", () =>
        {
            // A portrait picture letterboxed in a landscape client: the locator must find it.
            const int w = 400, h = 200;
            var pixels = new byte[w * h * 4];
            var pictureWidth = (int)Math.Round(h * 393.0 / 852.0);
            var left = (w - pictureWidth) / 2;
            for (var y = 0; y < h; y++)
            {
                for (var x = left; x < left + pictureWidth; x++)
                {
                    var i = ((y * w) + x) * 4;
                    pixels[i] = (byte)(60 + (x * 3 % 150));
                    pixels[i + 1] = (byte)(90 + (y % 120));
                    pixels[i + 2] = 200;
                    pixels[i + 3] = 255;
                }
            }

            var found = PictureLocator.Locate(pixels, w, h, w * 4, 393.0 / 852.0)?.Picture;
            Expect(found is { } r && Math.Abs(r.Left - left) <= 2 && Math.Abs(r.Width - pictureWidth) <= 2, $"found {found}");
            return Task.FromResult($"found {found}");
        }).ConfigureAwait(false);

        await Check("win32", () =>
        {
            var cursor = new Win32Desktop().CursorPosition;
            return Task.FromResult(string.Create(CultureInfo.InvariantCulture, $"cursor at ({cursor.X:0}, {cursor.Y:0})"));
        }).ConfigureAwait(false);

        await Check("keyboard", () =>
        {
            // The hook goes in and out (it never takes a key: nothing counts as the mirror window), and the PC's
            // layout turns keys into characters.
            var a = KeyboardHook.ToChar(VirtualKeys.A, 0x1E, shift: false);
            var shiftA = KeyboardHook.ToChar(VirtualKeys.A, 0x1E, shift: true);
            Expect(a == 'a' && shiftA == 'A', $"A -> {a}, Shift+A -> {shiftA}");
            var forwarder = new KeyboardForwarder(() => false, KeyboardHook.ToChar, _ => { });
            using (var hook = new KeyboardHook(forwarder))
            {
                hook.Start();
            }

            var key = forwarder.OnKey(VirtualKeys.A, 0x1E, true) ? "taken" : "left to Windows";
            return Task.FromResult($"hook installed and removed; A -> '{a}', Shift+A -> '{shiftA}', unfocused A {key}");
        }).ConfigureAwait(false);

        output.WriteLine(failures == 0 ? "SELF-CHECK PASSED" : $"SELF-CHECK FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
