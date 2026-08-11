using Flow.Launcher.Plugin.QuickUninstall.Models;

namespace Flow.Launcher.Plugin.QuickUninstall.Providers;

public interface IInstalledAppProvider
{
    Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken cancellationToken);
}
