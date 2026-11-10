using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TermKit.Services
{
    internal sealed record AdControllerProbe(IReadOnlyList<string> Reachable, IReadOnlyList<string> Problems);

    /// <summary>
    /// Finds domain controllers this machine can actually reach, so domain-joined and non-joined (Autopilot/hybrid) PCs both work.
    /// Configured controllers are probed first; DNS SRV discovery runs only when none are reachable.
    /// </summary>
    internal static class AdControllerDiscovery
    {
        private const int AdwsPort = 9389;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan SrvTimeout = TimeSpan.FromSeconds(8);

        internal static List<string> BuildCandidates(ActiveDirectoryConfig config, IEnumerable<string>? discovered = null)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(config.PrimaryDomainController)) list.Add(config.PrimaryDomainController.Trim());
            if (config.FallbackDomainControllers != null) list.AddRange(config.FallbackDomainControllers.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
            if (discovered != null) list.AddRange(discovered.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().TrimEnd('.')));
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // DomainDNS is often the UPN suffix, so the AD DNS domain is also derived from the controller FQDNs.
        internal static List<string> GetDnsDomains(ActiveDirectoryConfig config)
        {
            var domains = new List<string>();
            foreach (var controller in BuildCandidates(config))
            {
                if (IPAddress.TryParse(controller, out _)) continue;
                var dot = controller.IndexOf('.');
                if (dot > 0 && dot < controller.Length - 1) domains.Add(controller[(dot + 1)..]);
            }
            if (!string.IsNullOrWhiteSpace(config.DomainDNS)) domains.Add(config.DomainDNS.Trim());
            return domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static async Task<AdControllerProbe> ResolveAsync(ActiveDirectoryConfig config)
        {
            var problems = new List<string>();
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var reachable = await ProbeAsync(BuildCandidates(config), tried, problems);
            if (reachable.Count == 0)
            {
                var discovered = await DiscoverViaDnsAsync(GetDnsDomains(config));
                reachable = await ProbeAsync(discovered, tried, problems);
                if (reachable.Count > 0) Logger.Log($"Configured domain controllers were unreachable; using DNS-discovered controllers: {string.Join(", ", reachable)}", LogType.Warning);
            }

            return new AdControllerProbe(reachable, problems);
        }

        private static async Task<List<string>> ProbeAsync(IEnumerable<string> candidates, HashSet<string> tried, List<string> problems)
        {
            var pending = candidates.Where(tried.Add).ToList();
            var results = await Task.WhenAll(pending.Select(async c => (Controller: c, Problem: await ProbeOneAsync(c))));
            foreach (var r in results.Where(r => r.Problem != null)) problems.Add($"{r.Controller}: {r.Problem}");
            return results.Where(r => r.Problem == null).Select(r => r.Controller).ToList();
        }

        private static async Task<string?> ProbeOneAsync(string controller)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(controller).WaitAsync(ProbeTimeout);
                using var cts = new CancellationTokenSource(ProbeTimeout);
                using var client = new TcpClient();
                await client.ConnectAsync(addresses, AdwsPort, cts.Token);
                return null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
            {
                return "name did not resolve. Connect to the VPN/corporate network and make sure its DNS servers are in use.";
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return $"no response on port {AdwsPort} (AD Web Services). Check VPN or firewall.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static async Task<List<string>> DiscoverViaDnsAsync(IEnumerable<string> domains)
        {
            var found = new List<string>();
            foreach (var domain in domains)
            {
                try
                {
                    var names = await Task.Run(() =>
                    {
                        using var runspace = OnPremAdService.CreateRunspace();
                        using var ps = PowerShell.Create();
                        ps.Runspace = runspace;
                        ps.AddCommand("Resolve-DnsName")
                          .AddParameter("Name", $"_ldap._tcp.dc._msdcs.{domain}")
                          .AddParameter("Type", "SRV")
                          .AddParameter("DnsOnly")
                          .AddParameter("ErrorAction", "Stop");
                        return ps.Invoke()
                            .Where(r => string.Equals(r.Properties["Type"]?.Value?.ToString(), "SRV", StringComparison.OrdinalIgnoreCase))
                            .Select(r => r.Properties["NameTarget"]?.Value?.ToString())
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n!)
                            .ToList();
                    }).WaitAsync(SrvTimeout);
                    found.AddRange(names);
                }
                catch (Exception ex)
                {
                    Logger.Log($"DNS discovery for '{domain}' failed: {ex.Message}", LogType.Warning);
                }
            }

            return found.Select(n => n.Trim().TrimEnd('.')).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        }
    }
}
