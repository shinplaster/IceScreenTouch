namespace IceScreenTouch.Probe;

/// <summary>Which probe page a measurement needs.</summary>
public enum ProbePage
{
    /// <summary>The touch probe page (/touch): 9 taps, swipe, pinch.</summary>
    Touch,

    /// <summary>The scroll page (/scroll): wheel scrolls.</summary>
    Scroll,

    /// <summary>The latency page (/latency): black, white while touched; its clock is synced to the PC.</summary>
    Latency,

    /// <summary>The keyboard page (/keys): a text box the PC types into.</summary>
    Keys,
}

/// <summary>What still keeps a probe from starting (none = ready).</summary>
[Flags]
public enum ProbeMissing
{
    /// <summary>Ready.</summary>
    None = 0,

    /// <summary>The iPhone has not subscribed to the touch report.</summary>
    Subscription = 1,

    /// <summary>The page has never said hello.</summary>
    PageNotOpened = 2,

    /// <summary>The page was opened but is not in the foreground (or stopped saying hello).</summary>
    PageNotInFront = 4,

    /// <summary>The other probe page is open (/touch instead of /scroll, or the other way round).</summary>
    WrongPage = 8,

    /// <summary>The keyboard page is open but its text box does not have the cursor.</summary>
    FieldNotFocused = 16,
}

/// <summary>Decides whether the phone and the page are ready for a probe.</summary>
public static class ProbeReadiness
{
    /// <summary>The touch page says hello every 5 s while visible; older than this = not in front.</summary>
    public const double TouchPageFreshSeconds = 12;

    /// <summary>The scroll page says hello every 2 s while visible.</summary>
    public const double ScrollPageFreshSeconds = 6;

    /// <summary>The latency page says hello every 2 s while visible (after its clock sync).</summary>
    public const double LatencyPageFreshSeconds = 6;

    /// <summary>The keyboard page says hello every 2 s while visible (and at once when its box gains or loses the cursor).</summary>
    public const double KeysPageFreshSeconds = 6;

    /// <summary>The page path to open in Safari.</summary>
    public static string Path(ProbePage page) => page switch
    {
        ProbePage.Scroll => "/scroll",
        ProbePage.Latency => "/latency",
        ProbePage.Keys => "/keys",
        _ => "/touch",
    };

    /// <summary>
    /// Which page sent <paramref name="hello"/>: the latency and keyboard pages name themselves, the scroll page
    /// reports its scroll position.
    /// </summary>
    public static ProbePage PageOf(ProbeEvent hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        return hello.PageId switch
        {
            "latency" => ProbePage.Latency,
            "keys" => ProbePage.Keys,
            _ => hello.Sy is not null ? ProbePage.Scroll : ProbePage.Touch,
        };
    }

    /// <summary>What is missing, given the subscription, the page's last hello and the time now (<see cref="ProbeClock"/>).</summary>
    public static ProbeMissing Check(ProbePage page, bool subscribed, ProbeEvent? hello, double now)
    {
        var missing = subscribed ? ProbeMissing.None : ProbeMissing.Subscription;
        if (hello is null)
        {
            return missing | ProbeMissing.PageNotOpened;
        }

        if (PageOf(hello) != page)
        {
            return missing | ProbeMissing.WrongPage;
        }

        var fresh = page switch
        {
            ProbePage.Scroll => ScrollPageFreshSeconds,
            ProbePage.Latency => LatencyPageFreshSeconds,
            ProbePage.Keys => KeysPageFreshSeconds,
            _ => TouchPageFreshSeconds,
        };
        if (hello.State != "visible" || now - hello.T >= fresh)
        {
            missing |= ProbeMissing.PageNotInFront;
        }
        else if (page == ProbePage.Keys && hello.Focused != true)
        {
            missing |= ProbeMissing.FieldNotFocused;
        }

        return missing;
    }

    /// <summary>
    /// Whether <paramref name="page"/> is still open in front (and, for the keyboard page, with the cursor in its box):
    /// its last hello says visible and is recent. A probe checks this before each touch or key it sends, so that it
    /// stops when the phone switched to another app (security review, 2026-11-07).
    /// </summary>
    public static bool InFront(ProbePage page, ProbeServer server, double now)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Check(page, subscribed: true, server.HelloFor(page), now) == ProbeMissing.None;
    }

    /// <summary>English description for the console.</summary>
    public static string Describe(ProbeMissing missing, ProbePage page)
    {
        if (missing == ProbeMissing.None)
        {
            return "nothing";
        }

        var parts = new List<string>();
        if (missing.HasFlag(ProbeMissing.Subscription))
        {
            parts.Add("the iPhone has not subscribed to the touch report (pair / reconnect in Settings > Bluetooth)");
        }

        if (missing.HasFlag(ProbeMissing.PageNotOpened))
        {
            parts.Add($"the page {Path(page)} has not been opened");
        }

        if (missing.HasFlag(ProbeMissing.WrongPage))
        {
            parts.Add($"the other probe page is open; open {Path(page)}");
        }

        if (missing.HasFlag(ProbeMissing.PageNotInFront))
        {
            parts.Add("the page is not in the foreground");
        }

        if (missing.HasFlag(ProbeMissing.FieldNotFocused))
        {
            parts.Add("tap the text box on the page so the cursor is in it");
        }

        return string.Join("; ", parts);
    }
}
