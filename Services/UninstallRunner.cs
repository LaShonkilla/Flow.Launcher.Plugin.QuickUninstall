using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Flow.Launcher.Plugin.QuickUninstall.Models;

namespace Flow.Launcher.Plugin.QuickUninstall.Services;

public static class UninstallRunner
{
    private static readonly Regex GuidRegex = new(
        "^\\{[0-9A-Fa-f-]{36}\\}$",
        RegexOptions.Compiled);

    // No confirmation UI here. Confirmation is handled inside Flow Launcher by Main.cs.
    public static void Run(InstalledApp app)
    {
        switch (app.Source)
        {
            case AppSource.Steam:
                RunSteam(app);
                break;

            case AppSource.Store:
                RunAppx(app);
                break;

            default:
                RunProgram(app);
                break;
        }
    }

    private static void RunProgram(InstalledApp app)
    {
        // MSI entries often store /I even though the Control Panel Uninstall action
        // removes the product. Prefer explicit /X when we have a product-code key.
        if (app.WindowsInstaller &&
            !string.IsNullOrWhiteSpace(app.RegistryKeyName) &&
            GuidRegex.IsMatch(app.RegistryKeyName))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/x {app.RegistryKeyName}",
                UseShellExecute = true
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(app.UninstallCommand))
            throw new InvalidOperationException("No uninstall command was registered by this program.");

        var (fileName, arguments) = SplitCommand(app.UninstallCommand);

        // Also normalize MSI /I to /X when the raw registry command uses it.
        if (Path.GetFileName(fileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(fileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            arguments = Regex.Replace(arguments, @"(^|\s)/I(?=\s|\{)", "$1/X", RegexOptions.IgnoreCase);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        });
    }

    private static void RunSteam(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.SteamAppId))
            throw new InvalidOperationException("Steam AppID is missing.");

        Process.Start(new ProcessStartInfo
        {
            FileName = $"steam://uninstall/{app.SteamAppId}",
            UseShellExecute = true
        });
    }

    private static void RunAppx(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.PackageFullName))
            throw new InvalidOperationException("Package identity is missing.");

        var escaped = app.PackageFullName.Replace("'", "''");
        var script = $"Remove-AppxPackage -Package '{escaped}' -ErrorAction Stop";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static (string FileName, string Arguments) SplitCommand(string raw)
    {
        var command = Environment.ExpandEnvironmentVariables(raw.Trim());

        if (command.StartsWith('"'))
        {
            var closing = command.IndexOf('"', 1);
            if (closing > 1)
            {
                var file = command[1..closing];
                var args = command[(closing + 1)..].TrimStart();
                return (file, args);
            }
        }

        // Handles incorrectly-unquoted paths containing spaces better than splitting on first space.
        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            var end = exeIndex + 4;
            var file = command[..end].Trim().Trim('"');
            var args = command[end..].TrimStart();
            return (file, args);
        }

        var firstSpace = command.IndexOf(' ');
        if (firstSpace < 0)
            return (command.Trim('"'), string.Empty);

        return (
            command[..firstSpace].Trim().Trim('"'),
            command[(firstSpace + 1)..].TrimStart());
    }
}
