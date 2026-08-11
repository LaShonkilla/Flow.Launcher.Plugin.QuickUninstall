using Flow.Launcher.Plugin.QuickUninstall.Models;
using Flow.Launcher.Plugin.QuickUninstall.Providers;

namespace Flow.Launcher.Plugin.QuickUninstall.Services;

public sealed class AppCatalog
{
    private readonly IInstalledAppProvider[] _providers =
    {
        new RegistryProvider(),
        new AppxProvider(),
        new SteamProvider()
    };

    public async Task<IReadOnlyList<InstalledApp>> LoadAsync(CancellationToken cancellationToken)
    {
        var tasks = _providers.Select(p => SafeLoad(p, cancellationToken)).ToArray();
        var groups = await Task.WhenAll(tasks);

        var all = groups.SelectMany(x => x).ToList();

        // Keep the most useful native uninstall path when the same item appears twice.
        // Priority: Steam > Store > Program.
        return all
            .GroupBy(x => NormalizeName(x.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(x => SourcePriority(x.Source))
                .ThenByDescending(x => x.SizeBytes ?? 0)
                .First())
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<InstalledApp>> SafeLoad(
        IInstalledAppProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetAppsAsync(cancellationToken);
        }
        catch
        {
            return Array.Empty<InstalledApp>();
        }
    }

    private static int SourcePriority(AppSource source) => source switch
    {
        AppSource.Steam => 30,
        AppSource.Store => 20,
        AppSource.Program => 10,
        _ => 0
    };

    private static string NormalizeName(string name)
    {
        var chars = name
            .Trim()
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
