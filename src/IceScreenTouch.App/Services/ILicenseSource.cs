namespace IceScreenTouch.App.Services;

/// <summary>How a purchase request ended.</summary>
public enum PurchaseOutcome
{
    /// <summary>The user bought the app.</summary>
    Succeeded,

    /// <summary>The user already owns the app.</summary>
    AlreadyPurchased,

    /// <summary>The user closed the dialog without buying.</summary>
    NotPurchased,

    /// <summary>The Store could not be reached.</summary>
    NetworkError,

    /// <summary>The Store answered with an error.</summary>
    ServerError,

    /// <summary>The request could not be made (no window yet, product not found, exception).</summary>
    Failed,
}

/// <summary>
/// Reads the app's license from the platform's store and starts a purchase; implemented by the platform head
/// (Microsoft Store on Windows). Never shows UI except in <see cref="PurchaseAsync"/>.
/// </summary>
public interface ILicenseSource
{
    /// <summary>Raised (on any thread) when the platform says the license may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Gives the store the window that owns its dialogs. Call it as soon as the main window exists and again when
    /// the window is created anew; <see cref="PurchaseAsync"/> needs it. <paramref name="handle"/> is the
    /// native window handle.
    /// </summary>
    void AttachWindow(nint handle);

    /// <summary>Reads the license now. Failures give <see cref="LicenseState.Unknown"/> instead of throwing.</summary>
    Task<LicenseState> ReadAsync();

    /// <summary>
    /// Asks the store to buy the app (shows the store's dialog). Does not throw. Call it on the UI thread: the
    /// implementation requests the dialog from the thread it is called on and must not hop to another one on the way.
    /// </summary>
    Task<PurchaseOutcome> PurchaseAsync();
}
