using System.Diagnostics;
using AirPad.Core.Logging;

namespace AirPad.Core.Discovery;

/// <summary>
/// Manages Windows Firewall rules needed by the receiver:
/// TCP inbound on the AirPlay/AirTunes ports and UDP on mDNS (5353)
/// plus the RAOP audio ports. Requires elevation to apply.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class FirewallHelper
{
    public const string RuleName = "AirPad Wireless Display Receiver";

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string BuildAddRulesCommand(int airPlayPort, int airTunesPort, int audioControlPort, int audioDataPort)
    {
        return string.Join(" & ", new[]
        {
            $"netsh advfirewall firewall delete rule name=\"{RuleName}\"",
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={airPlayPort},{airTunesPort} profile=private,public",
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP localport=5353,{audioControlPort},{audioDataPort} profile=private,public",
        });
    }

    /// <summary>
    /// Tries to install the firewall rules. Returns false (with a message)
    /// when not elevated instead of throwing a UAC prompt from the library.
    /// </summary>
    public static bool TryEnsureRules(AirPadLogger log, int airPlayPort, int airTunesPort, int audioControlPort, int audioDataPort, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Firewall configuration is only supported on Windows.";
            return false;
        }
        if (!IsAdministrator())
        {
            message = "Administrator rights are required to configure Windows Firewall. Run scripts/setup-firewall.ps1 as Administrator, or click 'Fix Firewall' in Settings (elevated).";
            log.Warn(message);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                Arguments = $"advfirewall firewall delete rule name=\"{RuleName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (var p = Process.Start(psi)) p?.WaitForExit(10000);

            var rules = new[]
            {
                $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={airPlayPort},{airTunesPort} profile=private,public",
                $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP localport=5353,{audioControlPort},{audioDataPort} profile=private,public",
            };
            foreach (var args in rules)
            {
                var psi2 = new ProcessStartInfo("netsh")
                {
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                using var p2 = Process.Start(psi2);
                p2?.WaitForExit(15000);
                if (p2?.ExitCode != 0)
                {
                    message = $"netsh exited with code {p2?.ExitCode} for: {args}";
                    log.Error(message);
                    return false;
                }
            }

            message = "Windows Firewall rules installed.";
            log.Info(message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to configure Windows Firewall: {ex.Message}";
            log.Error(message, ex);
            return false;
        }
    }
}
