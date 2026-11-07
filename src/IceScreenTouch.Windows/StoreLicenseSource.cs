using System.Runtime.InteropServices;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Services;
using Windows.Services.Store;

namespace IceScreenTouch.Windows;

/// <summary>
/// The Microsoft Store license of the packaged build (<c>Windows.Services.Store</c>). Only created for a process with
/// a package identity; a build of your own never touches the Store. Reading the license shows no UI. A desktop app has
/// to give the Store its window (<c>InitializeWithWindow</c>) before a call that may show a dialog, so
/// <see cref="PurchaseAsync"/> is refused until <see cref="AttachWindow"/> has been called. Not run as administrator:
/// in-app purchases are not supported elevated, and nothing here elevates.
/// </summary>
internal sealed class StoreLicenseSource(AppLog? log = null) : ILicenseSource, IDisposable
{
    private readonly Lock _lock = new();
    private StoreContext? _context;
    private nint _window;
    private string? _lastSaid;

    public event EventHandler? Changed;

    public void AttachWindow(nint handle)
    {
        lock (_lock)
        {
            if (handle == _window)
            {
                return;
            }

            // A new window: the next call makes a context for it.
            _window = handle;
            DropContext();
        }
    }

    public async Task<LicenseState> ReadAsync()
    {
        try
        {
            var license = await GetContext(requireWindow: false).GetAppLicenseAsync().AsTask().ConfigureAwait(false);
            // What the Store says, without anything personal; written when it changes (the app reads it often).
            var said = $"active={license.IsActive}, trial={license.IsTrial}, sku id {(string.IsNullOrEmpty(license.SkuStoreId) ? "empty" : "set")}";
            if (Interlocked.Exchange(ref _lastSaid, said) != said)
            {
                log?.Write($"[lic ] Store license: {said}");
            }

            return LicenseState.From(license.IsActive, license.IsTrial, license.ExpirationDate);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException or TypeLoadException)
        {
            log?.Write($"[lic ] cannot read the Store license: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return LicenseState.Unknown;
        }
    }

    public async Task<PurchaseOutcome> PurchaseAsync()
    {
        try
        {
            // No ConfigureAwait(false) from here on: the caller is on the UI thread, and the Store's purchase dialog
            // must be requested from it (InitializeWithWindow was done for that thread's window).
            var context = GetContext(requireWindow: true);
            var found = await context.GetStoreProductForCurrentAppAsync().AsTask();
            if (found.Product is not { } product)
            {
                log?.Write($"[lic ] the Store product was not found: 0x{found.ExtendedError?.HResult ?? 0:X8}");
                return PurchaseOutcome.Failed;
            }

            var result = await product.RequestPurchaseAsync().AsTask();
            return result.Status switch
            {
                StorePurchaseStatus.Succeeded => PurchaseOutcome.Succeeded,
                StorePurchaseStatus.AlreadyPurchased => PurchaseOutcome.AlreadyPurchased,
                StorePurchaseStatus.NotPurchased => PurchaseOutcome.NotPurchased,
                StorePurchaseStatus.NetworkError => PurchaseOutcome.NetworkError,
                StorePurchaseStatus.ServerError => PurchaseOutcome.ServerError,
                _ => PurchaseOutcome.Failed,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException or TypeLoadException)
        {
            log?.Write($"[lic ] cannot buy: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return PurchaseOutcome.Failed;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DropContext();
        }
    }

    // The Store's context for this window. A purchase needs the window; a read works without one (no UI).
    private StoreContext GetContext(bool requireWindow)
    {
        lock (_lock)
        {
            if (requireWindow && _window == 0)
            {
                throw new InvalidOperationException("the main window has not been given to the Store yet");
            }

            if (_context is null)
            {
                var context = StoreContext.GetDefault();
                if (_window != 0)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(context, _window);
                }

                context.OfflineLicensesChanged += OnOfflineLicensesChanged;
                _context = context;
            }

            return _context;
        }
    }

    private void DropContext()
    {
        if (_context is { } context)
        {
            context.OfflineLicensesChanged -= OnOfflineLicensesChanged;
            _context = null;
        }
    }

    private void OnOfflineLicensesChanged(StoreContext sender, object args) => Changed?.Invoke(this, EventArgs.Empty);
}
