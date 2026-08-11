using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Flow.Launcher.Plugin.QuickUninstall.Models;

namespace Flow.Launcher.Plugin.QuickUninstall.Providers;

public sealed class SteamProvider : IInstalledAppProvider
{
    private static readonly Regex PairRegex = new(
        "\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"",
        RegexOptions.Compiled);

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<InstalledApp>>(() =>
        {
            var steamRoot = FindSteamRoot();
            if (steamRoot == null)
                return Array.Empty<InstalledApp>();

            var libraries = FindLibraries(steamRoot);
            var apps = new List<InstalledApp>();
            var steamExe = Path.Combine(steamRoot, "steam.exe");

            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var steamApps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamApps))
                    continue;

                foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var values = ParsePairs(File.ReadAllText(manifest));
                        if (!values.TryGetValue("appid", out var appId) ||
                            !values.TryGetValue("name", out var name) ||
                            string.IsNullOrWhiteSpace(appId) ||
                            string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        long? size = null;
                        if (values.TryGetValue("SizeOnDisk", out var sizeText) &&
                            long.TryParse(sizeText, out var sizeBytes))
                        {
                            size = sizeBytes;
                        }

                        DateTime? sortDate = null;
                        if (values.TryGetValue("LastUpdated", out var updatedText) &&
                            long.TryParse(updatedText, out var unixSeconds))
                        {
                            try
                            {
                                sortDate = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
                            }
                            catch
                            {
                                // Fall back to manifest timestamp below.
                            }
                        }

                        if (sortDate is null)
                        {
                            try
                            {
                                sortDate = File.GetLastWriteTime(manifest);
                            }
                            catch
                            {
                                // Date is optional.
                            }
                        }

                        apps.Add(new InstalledApp
                        {
                            Name = name.Trim(),
                            Publisher = "Steam",
                            Source = AppSource.Steam,
                            SteamAppId = appId.Trim(),
                            SizeBytes = size,
                            SortDate = sortDate,
                            IconPath = File.Exists(steamExe) ? steamExe : null
                        });
                    }
                    catch
                    {
                        // Ignore broken/incomplete manifests.
                    }
                }
            }

            return apps;
        }, cancellationToken);
    }

    private static string? FindSteamRoot()
    {
        var candidates = new List<string?>
        {
            ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        };

        return candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Replace('/', Path.DirectorySeparatorChar))
            .FirstOrDefault(Directory.Exists);
    }

    private static string? ReadRegistry(RegistryKey root, string path, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(valueName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> FindLibraries(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (!File.Exists(vdf))
            return libraries;

        try
        {
            var text = File.ReadAllText(vdf);
            foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
            {
                var path = match.Groups["path"].Value
                    .Replace("\\\\", "\\")
                    .Replace('/', Path.DirectorySeparatorChar);

                if (Directory.Exists(path))
                    libraries.Add(path);
            }
        }
        catch
        {
            // Default Steam root is still available.
        }

        return libraries;
    }

    private static Dictionary<string, string> ParsePairs(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PairRegex.Matches(text))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value;
            if (!values.ContainsKey(key))
                values[key] = value;
        }
        return values;
    }
}
