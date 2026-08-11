using System.IO;
using Microsoft.Win32;
using Flow.Launcher.Plugin.QuickUninstall.Models;

namespace Flow.Launcher.Plugin.QuickUninstall.Providers;

public sealed class RegistryProvider : IInstalledAppProvider
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<InstalledApp>>(() =>
        {
            var apps = new List<InstalledApp>();

            ReadHive(apps, RegistryHive.LocalMachine, RegistryView.Registry64, cancellationToken);
            ReadHive(apps, RegistryHive.LocalMachine, RegistryView.Registry32, cancellationToken);
            ReadHive(apps, RegistryHive.CurrentUser, RegistryView.Registry64, cancellationToken);
            ReadHive(apps, RegistryHive.CurrentUser, RegistryView.Registry32, cancellationToken);

            return apps;
        }, cancellationToken);
    }

    private static void ReadHive(
        List<InstalledApp> apps,
        RegistryHive hive,
        RegistryView view,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall == null)
                return;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    if (key == null)
                        continue;

                    var displayName = GetString(key, "DisplayName");
                    var uninstallString = GetString(key, "UninstallString");

                    if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(uninstallString))
                        continue;

                    if (GetInt(key, "SystemComponent") == 1 || GetInt(key, "NoRemove") == 1)
                        continue;

                    var releaseType = GetString(key, "ReleaseType");
                    if (!string.IsNullOrWhiteSpace(releaseType) &&
                        (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                         releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
                         releaseType.Contains("Security", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var estimatedSizeKb = GetLong(key, "EstimatedSize");

                    apps.Add(new InstalledApp
                    {
                        Name = displayName.Trim(),
                        Publisher = GetString(key, "Publisher"),
                        Version = GetString(key, "DisplayVersion"),
                        SizeBytes = estimatedSizeKb > 0 ? estimatedSizeKb * 1024L : null,
                        SortDate = ParseInstallDate(GetString(key, "InstallDate")),
                        Source = AppSource.Program,
                        IconPath = NormalizeDisplayIcon(GetString(key, "DisplayIcon")),
                        UninstallCommand = uninstallString.Trim(),
                        RegistryKeyName = subKeyName,
                        WindowsInstaller = GetInt(key, "WindowsInstaller") == 1
                    });
                }
                catch
                {
                    // Ignore broken/unreadable uninstall entries.
                }
            }
        }
        catch
        {
            // Registry view might not exist or might be inaccessible.
        }
    }

    private static string? GetString(RegistryKey key, string name)
        => key.GetValue(name)?.ToString();

    private static int GetInt(RegistryKey key, string name)
    {
        try { return Convert.ToInt32(key.GetValue(name) ?? 0); }
        catch { return 0; }
    }

    private static long GetLong(RegistryKey key, string name)
    {
        try { return Convert.ToInt64(key.GetValue(name) ?? 0L); }
        catch { return 0L; }
    }


    private static DateTime? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParseExact(
                value.Trim(),
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return parsed.Date;
        }

        return DateTime.TryParse(value, out parsed) ? parsed.Date : null;
    }

    private static string? NormalizeDisplayIcon(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));

        // Common form: C:\Path\app.exe,0
        var comma = value.LastIndexOf(',');
        if (comma > 1)
        {
            var possiblePath = value[..comma].Trim().Trim('"');
            if (File.Exists(possiblePath))
                value = possiblePath;
        }

        return File.Exists(value) ? value : null;
    }
}
