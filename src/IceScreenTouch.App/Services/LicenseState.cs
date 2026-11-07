namespace IceScreenTouch.App.Services;

/// <summary>Where the app stands with its Microsoft Store license.</summary>
public enum LicenseKind
{
    /// <summary>Not a Store package (a build of your own): fully usable, nothing is checked.</summary>
    NotPackaged,

    /// <summary>Bought.</summary>
    Purchased,

    /// <summary>The free trial is running; <see cref="LicenseState.Expiration"/> says until when.</summary>
    Trial,

    /// <summary>The trial ended and the app was not bought: the session must not run.</summary>
    Expired,

    /// <summary>The license could not be read (offline, Store error): treated as usable so that paying users are never locked out.</summary>
    Unknown,
}

/// <summary>
/// The license of the running app. The decision "may the session run" is <see cref="AllowsSession"/>, the one
/// place every session start asks.
/// </summary>
/// <param name="Kind">What kind of license.</param>
/// <param name="Expiration">The end of the trial (only for <see cref="LicenseKind.Trial"/>).</param>
public sealed record LicenseState(LicenseKind Kind, DateTimeOffset? Expiration = null)
{
    /// <summary>A build of your own.</summary>
    public static LicenseState NotPackaged { get; } = new(LicenseKind.NotPackaged);

    /// <summary>Bought.</summary>
    public static LicenseState Purchased { get; } = new(LicenseKind.Purchased);

    /// <summary>The trial ended.</summary>
    public static LicenseState Expired { get; } = new(LicenseKind.Expired);

    /// <summary>Could not be read.</summary>
    public static LicenseState Unknown { get; } = new(LicenseKind.Unknown);

    /// <summary>
    /// The session may run in every state but <see cref="LicenseKind.Expired"/>. Unknown fails open on purpose: a
    /// paying user who is offline must not be locked out.
    /// </summary>
    public bool AllowsSession => Kind != LicenseKind.Expired;

    /// <summary>A running trial.</summary>
    public static LicenseState Trial(DateTimeOffset expiration) => new(LicenseKind.Trial, expiration);

    /// <summary>
    /// Maps what the Store's app license says (<c>IsActive</c>, <c>IsTrial</c>, <c>ExpirationDate</c>). An active
    /// trial without a usable expiration cannot be timed, so it counts as unknown.
    /// </summary>
    public static LicenseState From(bool isActive, bool isTrial, DateTimeOffset expiration)
    {
        if (!isActive)
        {
            return Expired;
        }

        if (!isTrial)
        {
            return Purchased;
        }

        // The default value (year 1) means "no date".
        return expiration.Year > 1 ? Trial(expiration) : Unknown;
    }
}
