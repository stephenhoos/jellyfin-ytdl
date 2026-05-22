using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtdlArchive.Services;

public sealed class LibraryBootstrapHostedService : IHostedService
{
    private readonly ILogger<LibraryBootstrapHostedService> _logger;
    private readonly LibraryReconciler _libraryReconciler;

    public LibraryBootstrapHostedService(
        ILogger<LibraryBootstrapHostedService> logger,
        LibraryReconciler libraryReconciler)
    {
        _logger = logger;
        _libraryReconciler = libraryReconciler;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _libraryReconciler.EnsureConfiguredLibrariesAsync(cancellationToken).ConfigureAwait(false);

        _ = Task.Run(() => RefreshAfterStartupAsync(CancellationToken.None), CancellationToken.None);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RefreshAfterStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            await _libraryReconciler.RefreshLibraryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YtdlArchive could not queue the startup library refresh");
        }
    }
}
