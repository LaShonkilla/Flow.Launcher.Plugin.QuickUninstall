using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Flow.Launcher.Plugin.QuickUninstall.Models;
using Flow.Launcher.Plugin.QuickUninstall.Services;

namespace Flow.Launcher.Plugin.QuickUninstall;

public sealed class Main : IAsyncPlugin, IContextMenu
{
    private const string FallbackIcon = "icon.png";
    private const string StatsAppsIcon = "stats-apps.png";
    private const string StatsDriveIcon = "stats-drive.png";
    private const byte VkEscape = 0x1B;
    private const byte VkRight = 0x27;
    private const uint KeyEventKeyUp = 0x0002;

    // Stale-while-revalidate: queries always use the current in-memory catalog immediately.
    // A refresh may start in the background, but QueryAsync never waits for it.
    private static readonly TimeSpan ActivationRefreshDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumCacheAge = TimeSpan.FromSeconds(30);

    private enum SortMode
    {
        Default,
        DateNewest,
        DateOldest,
        SizeLargest,
        SizeSmallest,
        NameDescending
    }

    private readonly record struct ParsedSearch(SortMode Sort, string SearchText);

    private readonly AppCatalog _catalog = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private PluginInitContext _context = null!;
    private IReadOnlyList<InstalledApp> _apps = Array.Empty<InstalledApp>();
    private bool _loaded;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private DateTime _lastRefreshRequestUtc = DateTime.MinValue;
    private int _backgroundRefreshRunning;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public async Task InitAsync(PluginInitContext context)
    {
        _context = context;
        await RefreshAsync(CancellationToken.None);
        _lastRefreshRequestUtc = _lastRefreshUtc;
    }

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        var rawSearch = query.Search?.Trim() ?? string.Empty;

        // Never block the visible query on indexing. Show the current cache immediately,
        // while a stale-while-revalidate refresh runs independently in the background.
        ScheduleBackgroundRefresh(rawSearch);

        // Dedicated statistics view.
        if (rawSearch.Equals("stat", StringComparison.OrdinalIgnoreCase))
            return BuildStatisticsResults();

        var parsed = ParseSearch(rawSearch);
        var matches = new List<(InstalledApp App, Result Result, int FuzzyScore)>();

        foreach (var app in _apps)
        {
            token.ThrowIfCancellationRequested();

            var fuzzyScore = 100;

            if (!string.IsNullOrWhiteSpace(parsed.SearchText))
            {
                var nameMatch = _context.API.FuzzySearch(parsed.SearchText, app.Name);
                var publisherScore = !string.IsNullOrWhiteSpace(app.Publisher)
                    ? _context.API.FuzzySearch(parsed.SearchText, app.Publisher).Score
                    : 0;
                var sourceScore = _context.API.FuzzySearch(parsed.SearchText, app.SourceLabel).Score;

                fuzzyScore = Math.Max(nameMatch.Score, Math.Max(publisherScore, sourceScore));

                if (fuzzyScore <= 0)
                    continue;
            }

            var captured = app;

            var result = new Result
            {
                Title = app.Name,
                SubTitle = BuildSubtitle(app),
                IcoPath = !string.IsNullOrWhiteSpace(app.IconPath) ? app.IconPath : FallbackIcon,
                ContextData = app,
                Action = actionContext => OpenContextMenu()
            };

            matches.Add((app, result, fuzzyScore));
        }

        var ordered = OrderMatches(matches, parsed.Sort, parsed.SearchText);

        // Give the returned ordering explicit descending scores so Flow keeps our requested sort.
        var score = 100000;
        foreach (var item in ordered)
            item.Result.Score = score--;

        return ordered.Select(x => x.Result).ToList();
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult?.ContextData is not InstalledApp app)
            return new List<Result>();

        var captured = app;
        var icon = !string.IsNullOrWhiteSpace(app.IconPath) ? app.IconPath : FallbackIcon;

        return new List<Result>
        {
            new()
            {
                Title = "No — Cancel",
                SubTitle = "Return to the list",
                IcoPath = FallbackIcon,
                Action = actionContext => CancelContextMenu()
            },
            new()
            {
                Title = $"Yes — Uninstall {app.Name}",
                SubTitle = "Start the native uninstaller",
                IcoPath = icon,
                Action = actionContext => RunConfirmed(captured)
            }
        };
    }

    private static ParsedSearch ParseSearch(string rawSearch)
    {
        if (string.IsNullOrWhiteSpace(rawSearch))
            return new ParsedSearch(SortMode.Default, string.Empty);

        var parts = rawSearch.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return new ParsedSearch(SortMode.Default, string.Empty);

        var token = parts[0].Trim().ToLowerInvariant();
        var remaining = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        var sort = token switch
        {
            "-" => SortMode.NameDescending,
            "date" => SortMode.DateNewest,
            "-date" => SortMode.DateOldest,
            "size" => SortMode.SizeLargest,
            "-size" => SortMode.SizeSmallest,
            _ => SortMode.Default
        };

        return sort == SortMode.Default
            ? new ParsedSearch(SortMode.Default, rawSearch)
            : new ParsedSearch(sort, remaining);
    }

    private static List<(InstalledApp App, Result Result, int FuzzyScore)> OrderMatches(
        List<(InstalledApp App, Result Result, int FuzzyScore)> matches,
        SortMode sort,
        string searchText)
    {
        IEnumerable<(InstalledApp App, Result Result, int FuzzyScore)> ordered = sort switch
        {
            SortMode.DateNewest => matches
                .OrderByDescending(x => x.App.SortDate.HasValue)
                .ThenByDescending(x => x.App.SortDate)
                .ThenBy(x => GetNameSortKey(x.App.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase),

            SortMode.DateOldest => matches
                .OrderByDescending(x => x.App.SortDate.HasValue)
                .ThenBy(x => x.App.SortDate)
                .ThenBy(x => GetNameSortKey(x.App.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase),

            SortMode.SizeLargest => matches
                .OrderByDescending(x => x.App.SizeBytes.HasValue)
                .ThenByDescending(x => x.App.SizeBytes)
                .ThenBy(x => GetNameSortKey(x.App.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase),

            SortMode.SizeSmallest => matches
                .OrderByDescending(x => x.App.SizeBytes.HasValue)
                .ThenBy(x => x.App.SizeBytes)
                .ThenBy(x => GetNameSortKey(x.App.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase),

            SortMode.NameDescending => matches
                .OrderByDescending(x => GetNameSortKey(x.App.Name), StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(x => x.App.Name, StringComparer.OrdinalIgnoreCase),

            // Bare "un" defaults to A -> Z.
            _ when string.IsNullOrWhiteSpace(searchText) => matches
                .OrderBy(x => GetNameSortKey(x.App.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase),

            // Normal text search keeps fuzzy relevance as the primary order.
            _ => matches
                .OrderByDescending(x => x.FuzzyScore)
                .ThenBy(x => GetNameSortKey(x.App.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase)
        };

        return ordered.ToList();
    }

    private static string GetNameSortKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Normalize compatibility Unicode forms, remove invisible/control characters,
        // collapse whitespace, then ignore leading symbols/punctuation for human A-Z/Z-A sorting.
        var normalized = name.Normalize(NormalizationForm.FormKC);
        normalized = new string(normalized
            .Where(ch => !char.IsControl(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format)
            .ToArray());
        normalized = Regex.Replace(normalized.Trim(), @"\s+", " ");

        var firstLetterOrDigit = 0;
        while (firstLetterOrDigit < normalized.Length &&
               !char.IsLetterOrDigit(normalized[firstLetterOrDigit]))
        {
            firstLetterOrDigit++;
        }

        if (firstLetterOrDigit > 0 && firstLetterOrDigit < normalized.Length)
            normalized = normalized[firstLetterOrDigit..].TrimStart();

        return normalized;
    }

    private bool OpenContextMenu()
    {
        // Use the same Flow Launcher context menu for Enter and Right Arrow.
        // Returning false keeps Flow open; then the simulated Right Arrow opens
        // the context menu for the currently selected result without rebuilding
        // or reordering the result list.
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            keybd_event(VkRight, 0, 0, UIntPtr.Zero);
            keybd_event(VkRight, 0, KeyEventKeyUp, UIntPtr.Zero);
        });

        return false;
    }

    private bool CancelContextMenu()
    {
        // Flow currently doesn't expose an API that navigates back from an open context menu
        // after a context-menu result action. Simulate the same Esc key the user would press.
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
            keybd_event(VkEscape, 0, KeyEventKeyUp, UIntPtr.Zero);
        });

        return false;
    }

    private bool RunConfirmed(InstalledApp app)
    {
        try
        {
            UninstallRunner.Run(app);

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await RefreshAsync(CancellationToken.None);
            });

            // True lets Flow close normally after the uninstall command has started.
            return true;
        }
        catch (Exception ex)
        {
            // Use Flow's notification API instead of another modal popup.
            _context.API.ShowMsg(
                "Quick Uninstall",
                $"Could not start the uninstaller for {app.Name}. {ex.Message}",
                FallbackIcon);

            return false;
        }
    }

    private void ScheduleBackgroundRefresh(string rawSearch)
    {
        if (!_loaded)
            return;

        var now = DateTime.UtcNow;
        var catalogAge = now - _lastRefreshUtc;
        var sinceLastRequest = now - _lastRefreshRequestUtc;

        // A bare `un` is treated as opening the plugin. Direct/history searches also refresh
        // after the cache has aged, but none of these refreshes block QueryAsync.
        var shouldRefresh =
            (string.IsNullOrWhiteSpace(rawSearch) && sinceLastRequest >= ActivationRefreshDebounce)
            || catalogAge >= MaximumCacheAge;

        if (!shouldRefresh)
            return;

        // Record the request before checking the worker so repeated QueryAsync calls do not
        // continuously queue refreshes while the same background scan is still running.
        _lastRefreshRequestUtc = now;

        if (Interlocked.CompareExchange(ref _backgroundRefreshRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var changed = await RefreshAsync(CancellationToken.None);

                if (changed)
                    TryRequeryCurrentView();
            }
            catch
            {
                // Background refresh is best-effort. Keep serving the last known-good cache.
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundRefreshRunning, 0);
            }
        });
    }

    private async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var refreshedApps = await _catalog.LoadAsync(cancellationToken);
            var changed = _loaded && !CatalogsAreEquivalent(_apps, refreshedApps);

            _apps = refreshedApps;
            _loaded = true;
            _lastRefreshUtc = DateTime.UtcNow;

            return changed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static bool CatalogsAreEquivalent(
        IReadOnlyList<InstalledApp> current,
        IReadOnlyList<InstalledApp> refreshed)
    {
        if (current.Count != refreshed.Count)
            return false;

        // Compare stable uninstall identities rather than list order. This catches installs,
        // removals and package changes without refreshing the UI for harmless metadata changes.
        var currentIds = current.Select(GetCatalogIdentity)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var refreshedIds = refreshed.Select(GetCatalogIdentity)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        return currentIds.SequenceEqual(refreshedIds, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetCatalogIdentity(InstalledApp app)
    {
        var nativeId = app.Source switch
        {
            AppSource.Steam => app.SteamAppId,
            AppSource.Store => app.PackageFullName,
            AppSource.Program => app.RegistryKeyName ?? app.UninstallCommand,
            _ => null
        };

        return $"{app.Source}|{app.Name}|{nativeId}";
    }

    private void TryRequeryCurrentView()
    {
        try
        {
            // Newer Flow versions expose ReQuery(bool reselect). Use reflection so the plugin
            // remains build-compatible with the existing Flow.Launcher.Plugin 4.4.0 reference.
            // ReQuery keeps whatever the user is currently typing, unlike writing an old query
            // back with ChangeQuery.
            var api = _context.API;
            var apiType = api.GetType();

            var withReselect = apiType.GetMethod("ReQuery", new[] { typeof(bool) });
            if (withReselect != null)
            {
                withReselect.Invoke(api, new object[] { false });
                return;
            }

            var parameterless = apiType.GetMethod("ReQuery", Type.EmptyTypes);
            parameterless?.Invoke(api, null);
        }
        catch
        {
            // On older Flow versions the refreshed cache will simply be visible on the next
            // keystroke/query. Never overwrite the user's current text as a fallback.
        }
    }

    private List<Result> BuildStatisticsResults()
    {
        var results = new List<Result>
        {
            new()
            {
                Title = "Installed items",
                SubTitle = $"{_apps.Count} items",
                IcoPath = StatsAppsIcon,
                Score = 100000,
                Action = actionContext => false
            }
        };

        var score = 99999;

        foreach (var drive in DriveInfo.GetDrives()
                     .Where(drive => drive.IsReady)
                     .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = Math.Max(0, total - free);
                var driveName = drive.Name.TrimEnd('\\');

                results.Add(new Result
                {
                    Title = $"{driveName} Drive",
                    SubTitle = $"{FormatDriveSize(used)} used  •  {FormatDriveSize(free)} free  |  {FormatDriveSize(total)} total",
                    IcoPath = StatsDriveIcon,
                    Score = score--,
                    Action = actionContext => false
                });
            }
            catch
            {
                // Ignore drives that become unavailable while the query is being built.
            }
        }

        return results;
    }

    private static string FormatDriveSize(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double tb = gb * 1024d;

        if (bytes >= tb)
            return $"{bytes / tb:0.##} TB";

        return $"{bytes / gb:0.#} GB";
    }

    private static string BuildSubtitle(InstalledApp app)
    {
        var sourceAndPublisher = app.SourceLabel;

        if (!string.IsNullOrWhiteSpace(app.Publisher) &&
            !app.Publisher.Equals(app.SourceLabel, StringComparison.OrdinalIgnoreCase) &&
            !app.Publisher.Equals("Steam", StringComparison.OrdinalIgnoreCase))
        {
            sourceAndPublisher += $"  •  {CompactPublisher(app.Publisher)}";
        }

        var size = app.SizeBytes is > 0
            ? FormatSize(app.SizeBytes.Value)
            : "No size";

        var date = app.SortDate.HasValue
            ? app.SortDate.Value.ToString("dd.MM.yyyy")
            : "Date unknown";

        return $"{sourceAndPublisher}  |  {size}  •  {date}";
    }

    private static string CompactPublisher(string publisher)
    {
        var cleaned = Regex.Replace(publisher.Trim(), @"\s+", " ");

        if (cleaned.Length <= 30)
            return cleaned;

        // Maximum visible length is 30 characters, including the ellipsis.
        return cleaned[..29].TrimEnd() + "…";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit >= 3 ? $"{value:0.#} {units[unit]}" : $"{value:0} {units[unit]}";
    }
}
