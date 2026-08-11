namespace Flow.Launcher.Plugin.QuickUninstall.Models;

public enum AppSource
{
    Program,
    Store,
    Steam
}

public sealed class InstalledApp
{
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public long? SizeBytes { get; init; }
    public DateTime? SortDate { get; init; }
    public AppSource Source { get; init; }
    public string? IconPath { get; init; }

    // Program/MSI
    public string? UninstallCommand { get; init; }
    public string? RegistryKeyName { get; init; }
    public bool WindowsInstaller { get; init; }

    // MSIX/AppX
    public string? PackageFullName { get; init; }

    // Steam
    public string? SteamAppId { get; init; }

    public string SourceLabel => Source switch
    {
        AppSource.Program => "Program",
        AppSource.Store => "App",
        AppSource.Steam => "Steam",
        _ => "App"
    };
}
