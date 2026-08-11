using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Flow.Launcher.Plugin.QuickUninstall.Models;

namespace Flow.Launcher.Plugin.QuickUninstall.Providers;

public sealed class AppxProvider : IInstalledAppProvider
{
    private sealed class AppxDto
    {
        public string? Name { get; set; }
        public string? PackageFullName { get; set; }
        public string? Publisher { get; set; }
        public string? InstallLocation { get; set; }
        public string? Version { get; set; }
    }

    public async Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken cancellationToken)
    {
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$startApps = @{}
Get-StartApps | ForEach-Object { $startApps[$_.AppID] = $_.Name }

$items = Get-AppxPackage | Where-Object {
    $_.PackageFullName -and
    $_.IsFramework -eq $false -and
    $_.IsResourcePackage -eq $false -and
    $_.NonRemovable -ne $true
} | ForEach-Object {
    $p = $_
    $friendly = $null

    foreach ($entry in $startApps.GetEnumerator()) {
        if ($entry.Key -like ($p.PackageFamilyName + '!*')) {
            $friendly = $entry.Value
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($friendly)) {
        $friendly = $p.Name
    }

    [PSCustomObject]@{
        Name = $friendly
        PackageFullName = $p.PackageFullName
        Publisher = $p.Publisher
        InstallLocation = $p.InstallLocation
        Version = $p.Version.ToString()
    }
}

ConvertTo-Json -InputObject @($items) -Compress -Depth 4
";

        try
        {
            var json = await RunPowerShellAsync(script, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<InstalledApp>();

            var dtos = JsonSerializer.Deserialize<List<AppxDto>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            return dtos
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.PackageFullName))
                .Select(x => new InstalledApp
                {
                    Name = x.Name!.Trim(),
                    Publisher = FriendlyPublisher(x.Publisher),
                    Version = x.Version,
                    SortDate = TryGetInstallDate(x.InstallLocation),
                    Source = AppSource.Store,
                    PackageFullName = x.PackageFullName,
                    IconPath = TryResolveAppxIcon(x.InstallLocation)
                })
                .ToList();
        }
        catch
        {
            return Array.Empty<InstalledApp>();
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process == null)
            return string.Empty;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        return await outputTask;
    }


    private static DateTime? TryGetInstallDate(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            return null;

        try
        {
            var created = Directory.GetCreationTime(installLocation);
            if (created.Year > 1980)
                return created;

            var modified = Directory.GetLastWriteTime(installLocation);
            return modified.Year > 1980 ? modified : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveAppxIcon(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            return null;

        try
        {
            var manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
                return null;

            var doc = XDocument.Load(manifestPath);
            var visual = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            if (visual == null)
                return null;

            var logo = visual.Attributes().FirstOrDefault(a =>
                a.Name.LocalName is "Square44x44Logo" or "Square30x30Logo" or "Logo")?.Value;

            if (string.IsNullOrWhiteSpace(logo))
                return null;

            var exact = Path.Combine(installLocation, logo.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(exact))
                return exact;

            var dir = Path.GetDirectoryName(exact);
            var stem = Path.GetFileNameWithoutExtension(exact);
            var ext = Path.GetExtension(exact);

            if (dir != null && Directory.Exists(dir))
            {
                return Directory.GetFiles(dir, stem + "*" + ext)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();
            }
        }
        catch
        {
            // WindowsApps can be protected; fallback icon is fine.
        }

        return null;
    }

    private static string? FriendlyPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
            return null;

        // Publisher is often a certificate DN. Keep only CN where possible.
        var cn = publisher.Split(',')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));

        return cn?.Length > 3 ? cn[3..] : publisher;
    }
}
