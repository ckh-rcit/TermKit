using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public class RecipientInfo
    {
        public string? Name { get; set; }
        public string? Alias { get; set; }
        public string? DistinguishedName { get; set; }
        public string? Guid { get; set; }
        public string? PrimarySmtpAddress { get; set; }
        public string? UserPrincipalName { get; set; }
        public string? Identity { get; set; }
        public string? RecipientTypeDetails { get; set; }
        public string? RecipientType { get; set; }

        public IEnumerable<string> Keys(string requestedIdentity) =>
            new[] { requestedIdentity, Name, Alias, DistinguishedName, Guid, PrimarySmtpAddress, UserPrincipalName, Identity }
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!);
    }

    /// <summary>
    /// Snapshot of a Distribution Group's / Mail-Enabled Security Group's Delivery management, Message
    /// approval, and Membership approvals settings, as surfaced by the EAC edit panels of the same names.
    /// </summary>
    public class DistributionGroupSettings
    {
        public bool RequireSenderAuthenticationEnabled { get; set; } = true;
        public string[] AcceptMessagesOnlyFromSendersOrMembers { get; set; } = Array.Empty<string>();
        public bool ModerationEnabled { get; set; }
        public string[] ModeratedBy { get; set; } = Array.Empty<string>();
        public string[] BypassModerationFromSendersOrMembers { get; set; } = Array.Empty<string>();
        public string SendModerationNotifications { get; set; } = "Always";
        public string MemberJoinRestriction { get; set; } = "Open";
        public string MemberDepartRestriction { get; set; } = "Open";
        public string[] GrantSendOnBehalfTo { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Hosts a real Exchange Online PowerShell session (ExchangeOnlineManagement module) in-process,
    /// ported from Entra Scout's ExoPowerShellService and extended with the cmdlet wrappers needed by
    /// the ported Exchange Admin Toolkit tabs (mailbox/resource permissions, distribution group
    /// membership/ownership, recipient lookup, Bookings OWA policy). All PowerShell work runs on a
    /// single dedicated STA thread, matching Connect-ExchangeOnline's interactive sign-in requirements.
    /// </summary>
    public class ExoPowerShellService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        private readonly BlockingCollection<Action> _workQueue = new();
        private Thread? _staThread;
        private Runspace? _runspace;
        private static bool _consoleAllocated;

        public ExoPowerShellService() { }
        internal ExoPowerShellService(Runspace runspace) { _runspace = runspace; }

        public async Task ShutdownAsync()
        {
            await DisconnectAsync();
            await RunOnStaThreadAsync(() => { _runspace?.Dispose(); _runspace = null; });
            _workQueue.CompleteAdding();
        }

        public bool IsConnected { get; private set; }
        public string? ConnectedUserPrincipalName { get; private set; }
        public string? ConnectedTenantId { get; private set; }
        public string? LastError { get; private set; }

        public Task<bool> ConnectAsync() => RunOnStaThreadAsync(() =>
        {
            try
            {
                LastError = null;
                Logger.Log("Connecting to Exchange Online (PowerShell)...");
                EnsureRunspaceOpen();

                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                                        ps.AddScript(PowerShellPrerequisites.Script);
                    ps.Invoke();
                    LogPipelineErrors(ps, "Import-Module ExchangeOnlineManagement");

                    if (ps.HadErrors)
                    {
                        throw BuildPipelineException(ps);
                    }
                }

                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddCommand("Connect-ExchangeOnline")
                      .AddParameter("DisableWAM")
                      .AddParameter("SkipLoadingFormatData", true)
                      .AddParameter("ShowBanner", false)
                      .AddParameter("ErrorAction", "Stop");
                    ps.Invoke();
                    LogPipelineErrors(ps, "Connect-ExchangeOnline");
                    if (ps.HadErrors) throw BuildPipelineException(ps);

                    IsConnected = !ps.HadErrors;
                }

                if (IsConnected)
                {
                    using var ps = PowerShell.Create();
                    ps.Runspace = _runspace;
                    ps.AddCommand("Get-ConnectionInformation").AddParameter("ErrorAction", "Stop");
                    var results = ps.Invoke();
                    if (ps.HadErrors) throw BuildPipelineException(ps);
                    if (results.Count != 1) throw new InvalidOperationException("Expected one Exchange Online connection. Disconnect and reconnect.");
                    ValidateConnection(results[0]);
                    if (results.Count > 0)
                    {
                        ConnectedUserPrincipalName = results[0].Properties["UserPrincipalName"]?.Value?.ToString();
                        ConnectedTenantId = results[0].Properties["TenantID"]?.Value?.ToString();
                    }
                    Logger.Log($"Successfully connected to Exchange Online{(ConnectedUserPrincipalName != null ? $" as {ConnectedUserPrincipalName}" : string.Empty)}.", LogType.Success);
                }

                return IsConnected;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ConnectedUserPrincipalName = null;
                ConnectedTenantId = null;
                Logger.Log($"EXO connection failed: {ex.Message}", LogType.Error);
                IsConnected = false;
                return false;
            }
        });

        public Task DisconnectAsync() => RunOnStaThreadAsync(() =>
        {
            try
            {
                if (_runspace != null && IsConnected)
                {
                    using var ps = PowerShell.Create();
                    ps.Runspace = _runspace;
                    ps.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("Confirm", false)
                      .AddParameter("ErrorAction", "SilentlyContinue");
                    ps.Invoke();
                    Logger.Log("Disconnected from Exchange Online.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"EXO disconnect error: {ex.Message}", LogType.Error);
            }
            finally
            {
                IsConnected = false;
                ConnectedUserPrincipalName = null;
                ConnectedTenantId = null;
            }
        });

        internal static void ValidateConnection(PSObject connection)
        {
            if (connection.Properties["State"]?.Value?.ToString() != "Connected" ||
                connection.Properties["TokenStatus"]?.Value?.ToString() != "Active" ||
                !Guid.TryParse(connection.Properties["TenantID"]?.Value?.ToString(), out _))
                throw new InvalidOperationException("Exchange Online did not return an active token and tenant identity. Disconnect and reconnect.");
        }

        // --- Recipient / Mailbox lookups ---

        /// <summary>
        /// Returns the tenant's accepted domains (Get-AcceptedDomain), used to populate the
        /// TLD/domain dropdown on the Create Mailbox section. The default domain is returned first.
        /// </summary>
        public Task<string[]> GetAcceptedDomainsAsync() => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-AcceptedDomain").AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);

            var domains = results
                .Select(o => new
                {
                    Name = o.Properties["DomainName"]?.Value?.ToString(),
                    IsDefault = o.Properties["Default"]?.Value is bool b && b
                })
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .OrderByDescending(d => d.IsDefault)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.Name!)
                .ToArray();

            return domains;
        });

        public Task<RecipientInfo?> GetRecipientAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Recipient").AddParameter("Identity", identity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors || results.Count == 0) return null;
            return ToRecipientInfo(results[0]);
        });

        public Task<bool> MailboxExistsAsync(string identity, bool resourceOnly = false) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Mailbox").AddParameter("Identity", identity);
            if (resourceOnly)
            {
                ps.AddParameter("RecipientTypeDetails", new[] { "RoomMailbox", "EquipmentMailbox" });
            }
            ps.AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            return !ps.HadErrors && results.Count > 0;
        });

        public Task<string> GetRecipientTypeAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-EXORecipient").AddParameter("Identity", identity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors || results.Count == 0) return "Not Found";
            return results[0].Properties["RecipientTypeDetails"]?.Value?.ToString() ?? "Unknown";
        });

        // --- Full Access / Send As / Send on Behalf ---

        public Task<bool> HasFullAccessAsync(string mailboxIdentity, string userIdentity, RecipientInfo userObject) =>
            PermissionVerification.ReadAsync(() => HasFullAccessCoreAsync(mailboxIdentity, userIdentity, userObject));

        private Task<bool> HasFullAccessCoreAsync(string mailboxIdentity, string userIdentity, RecipientInfo userObject) => RunOnStaThreadAsync(() =>
        {
            var userKeys = new HashSet<string>(userObject.Keys(userIdentity), StringComparer.OrdinalIgnoreCase);

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand("Get-EXOMailboxPermission").AddParameter("Identity", mailboxIdentity).AddParameter("ErrorAction", "Stop");
                var results = ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);
                foreach (var r in results)
                {
                    var accessRights = r.Properties["AccessRights"]?.Value;
                    var deny = r.Properties["Deny"]?.Value is bool d && d;
                    var user = r.Properties["User"]?.Value?.ToString() ?? string.Empty;
                    if (!deny && userKeys.Contains(user) && PermissionVerification.HasAccessRight(accessRights, "FullAccess"))
                    {
                        return true;
                    }
                }
            }

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand("Get-MailboxPermission").AddParameter("Identity", mailboxIdentity).AddParameter("User", userIdentity).AddParameter("ErrorAction", "Stop");
                var results = ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);
                foreach (var r in results)
                {
                    var accessRights = r.Properties["AccessRights"]?.Value;
                    var deny = r.Properties["Deny"]?.Value is bool d && d;
                    if (!deny && PermissionVerification.HasAccessRight(accessRights, "FullAccess"))
                    {
                        return true;
                    }
                }
            }

            return false;
        });

        public Task AddFullAccessAsync(string mailboxIdentity, string userIdentity) =>
            PermissionVerification.ApplyAsync(() => AddFullAccessCoreAsync(mailboxIdentity, userIdentity), () => HasDirectFullAccessAsync(mailboxIdentity, userIdentity), true);

        private Task AddFullAccessCoreAsync(string mailboxIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Add-MailboxPermission")
              .AddParameter("Identity", mailboxIdentity)
              .AddParameter("User", userIdentity)
              .AddParameter("AccessRights", "FullAccess")
              .AddParameter("InheritanceType", "All")
              .AddParameter("Automapping", true)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task RemoveFullAccessAsync(string mailboxIdentity, string userIdentity) =>
            PermissionVerification.ApplyAsync(() => RemoveFullAccessCoreAsync(mailboxIdentity, userIdentity), () => HasDirectFullAccessAsync(mailboxIdentity, userIdentity), false);

        private Task RemoveFullAccessCoreAsync(string mailboxIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Remove-MailboxPermission")
              .AddParameter("Identity", mailboxIdentity)
              .AddParameter("User", userIdentity)
              .AddParameter("AccessRights", "FullAccess")
              .AddParameter("InheritanceType", "All")
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task<bool> HasSendAsAsync(string mailboxIdentity, string userIdentity) =>
            PermissionVerification.ReadAsync(() => HasSendAsCoreAsync(mailboxIdentity, userIdentity));

        private Task<bool> HasSendAsCoreAsync(string mailboxIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-RecipientPermission").AddParameter("Identity", mailboxIdentity).AddParameter("Trustee", userIdentity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
            return results.Any(r =>
            {
                var accessRights = r.Properties["AccessRights"]?.Value;
                var deny = r.Properties["AccessControlType"]?.Value?.ToString() == "Deny";
                return !deny && PermissionVerification.HasAccessRight(accessRights, "SendAs");
            });
        });

        public Task AddSendAsAsync(string mailboxIdentity, string userIdentity) =>
            PermissionVerification.ApplyAsync(() => AddSendAsCoreAsync(mailboxIdentity, userIdentity), () => HasSendAsAsync(mailboxIdentity, userIdentity), true);

        private Task AddSendAsCoreAsync(string mailboxIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Add-RecipientPermission")
              .AddParameter("Identity", mailboxIdentity)
              .AddParameter("Trustee", userIdentity)
              .AddParameter("AccessRights", "SendAs")
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task RemoveSendAsAsync(string mailboxIdentity, string userIdentity) =>
            PermissionVerification.ApplyAsync(() => RemoveSendAsCoreAsync(mailboxIdentity, userIdentity), () => HasSendAsAsync(mailboxIdentity, userIdentity), false);

        private Task RemoveSendAsCoreAsync(string mailboxIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Remove-RecipientPermission")
              .AddParameter("Identity", mailboxIdentity)
              .AddParameter("Trustee", userIdentity)
              .AddParameter("AccessRights", "SendAs")
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task<bool> HasSendOnBehalfAsync(string mailboxIdentity, RecipientInfo userObject) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Mailbox").AddParameter("Identity", mailboxIdentity).AddParameter("ErrorAction", "SilentlyContinue");
            var results = ps.Invoke();
            if (results.Count == 0) return false;

            var delegates = results[0].Properties["GrantSendOnBehalfTo"]?.Value as IEnumerable<object>;
            if (delegates == null) return false;

            foreach (var delegateObj in delegates)
            {
                var delegateStr = delegateObj?.ToString();
                if (!string.IsNullOrEmpty(delegateStr) &&
                    (string.Equals(delegateStr, userObject.DistinguishedName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(delegateStr, userObject.Guid, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        });

        public Task AddSendOnBehalfAsync(string mailboxIdentity, string userIdentity) =>
            AddSendOnBehalfBatchAsync(mailboxIdentity, new[] { userIdentity });

        public Task RemoveSendOnBehalfAsync(string mailboxIdentity, string userIdentity) =>
            RemoveSendOnBehalfBatchAsync(mailboxIdentity, new[] { userIdentity });

        /// <summary>
        /// Applies GrantSendOnBehalfTo Add for multiple users in a single Set-Mailbox call, matching
        /// the script's batched "Apply Send on Behalf changes in batch" step.
        /// </summary>
        public Task AddSendOnBehalfBatchAsync(string mailboxIdentity, string[] userIdentities) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            var addHash = new Hashtable { { "Add", userIdentities } };
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", mailboxIdentity)
              .AddParameter("GrantSendOnBehalfTo", addHash)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task RemoveSendOnBehalfBatchAsync(string mailboxIdentity, string[] userIdentities) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            var removeHash = new Hashtable { { "Remove", userIdentities } };
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", mailboxIdentity)
              .AddParameter("GrantSendOnBehalfTo", removeHash)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        // --- Distribution Groups ---

        public Task<string?> GetDistributionGroupTypeAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Recipient").AddParameter("Identity", identity).AddParameter("ErrorAction", "SilentlyContinue");
            var results = ps.Invoke();
            if (results.Count == 0) return null;
            return results[0].Properties["RecipientTypeDetails"]?.Value?.ToString();
        });

        public Task<bool> IsDistributionGroupMemberAsync(string groupIdentity, RecipientInfo userObject, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            var userKeys = new HashSet<string>(userObject.Keys(userIdentity), StringComparer.OrdinalIgnoreCase);
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-DistributionGroupMember").AddParameter("Identity", groupIdentity).AddParameter("ResultSize", "Unlimited").AddParameter("ErrorAction", "SilentlyContinue");
            var results = ps.Invoke();
            foreach (var r in results)
            {
                foreach (var propName in new[] { "PrimarySmtpAddress", "Alias", "DistinguishedName", "Guid", "Name" })
                {
                    var val = r.Properties[propName]?.Value?.ToString();
                    if (val != null && userKeys.Contains(val)) return true;
                }
            }
            return false;
        });

        public Task<bool> IsDistributionGroupOwnerAsync(string groupIdentity, RecipientInfo userObject, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            var userKeys = new HashSet<string>(userObject.Keys(userIdentity), StringComparer.OrdinalIgnoreCase);
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-DistributionGroup").AddParameter("Identity", groupIdentity).AddParameter("ErrorAction", "SilentlyContinue");
            var results = ps.Invoke();
            if (results.Count == 0) return false;
            var managedBy = results[0].Properties["ManagedBy"]?.Value as IEnumerable<object>;
            if (managedBy == null) return false;
            return managedBy.Any(m => m != null && userKeys.Contains(m.ToString()!));
        });

        public Task AddDistributionGroupMemberAsync(string groupIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Add-DistributionGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Member", userIdentity)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task RemoveDistributionGroupMemberAsync(string groupIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Remove-DistributionGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Member", userIdentity)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task AddDistributionGroupOwnerAsync(string groupIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            var addHash = new Hashtable { { "Add", userIdentity } };
            ps.AddCommand("Set-DistributionGroup")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("ManagedBy", addHash)
              .AddParameter("BypassSecurityGroupManagerCheck", true)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task RemoveDistributionGroupOwnerAsync(string groupIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            var removeHash = new Hashtable { { "Remove", userIdentity } };
            ps.AddCommand("Set-DistributionGroup")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("ManagedBy", removeHash)
              .AddParameter("BypassSecurityGroupManagerCheck", true)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        // --- Bookings ---

        public Task<string?> GetOwaMailboxPolicyAsync(string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-CASMailbox").AddParameter("Identity", userIdentity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
            if (results.Count == 0) return null;
            return results[0].Properties["OwaMailboxPolicy"]?.Value?.ToString();
        });

        public Task SetOwaMailboxPolicyAsync(string userIdentity, string owaPolicyName) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-CASMailbox")
              .AddParameter("Identity", userIdentity)
              .AddParameter("OwaMailboxPolicy", owaPolicyName)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        // --- Reporting ---

        /// <summary>
        /// Returns (DisplayName, PrimarySmtpAddress) for each Owner/Member of an M365 (Unified) Group,
        /// ported from M365_Reporting_Tool.ps1's Get-UnifiedGroupLinks usage.
        /// </summary>
        public Task<List<(string DisplayName, string PrimarySmtpAddress, string Role)>> GetUnifiedGroupLinksAsync(string groupIdentity) => RunOnStaThreadAsync(() =>
        {
            var rows = new List<(string, string, string)>();
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand("Get-UnifiedGroupLinks").AddParameter("Identity", groupIdentity).AddParameter("LinkType", "Owners").AddParameter("ResultSize", "Unlimited").AddParameter("ErrorAction", "Stop");
                foreach (var r in ps.Invoke())
                {
                    rows.Add((r.Properties["DisplayName"]?.Value?.ToString() ?? string.Empty, r.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? string.Empty, "Owner"));
                }
                if (ps.HadErrors) throw BuildPipelineException(ps);
            }

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand("Get-UnifiedGroupLinks").AddParameter("Identity", groupIdentity).AddParameter("LinkType", "Members").AddParameter("ResultSize", "Unlimited").AddParameter("ErrorAction", "Stop");
                foreach (var r in ps.Invoke())
                {
                    rows.Add((r.Properties["DisplayName"]?.Value?.ToString() ?? string.Empty, r.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? string.Empty, "Member"));
                }
                if (ps.HadErrors) throw BuildPipelineException(ps);
            }

            return rows;
        });

        /// <summary>
        /// Returns the ManagedBy owners and membership of a Distribution Group / Dynamic Distribution
        /// Group / Mail-Enabled Security Group, ported from the corresponding branches of
        /// M365_Reporting_Tool.ps1 (Get-DistributionGroup / Get-DynamicDistributionGroup + members).
        /// </summary>
        public Task<List<(string DisplayName, string PrimarySmtpAddress, string Role)>> GetDistributionGroupReportLinksAsync(string groupIdentity, bool isDynamic) => RunOnStaThreadAsync(() =>
        {
            var rows = new List<(string, string, string)>();
            string[] managedBy;

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand(isDynamic ? "Get-DynamicDistributionGroup" : "Get-DistributionGroup").AddParameter("Identity", groupIdentity).AddParameter("ErrorAction", "Stop");
                var results = ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);
                managedBy = (results.Count > 0 ? results[0].Properties["ManagedBy"]?.Value as IEnumerable : null)?.Cast<object>().Select(o => o?.ToString() ?? string.Empty).ToArray() ?? Array.Empty<string>();
            }

            foreach (var owner in managedBy)
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;
                ps.AddCommand("Get-Recipient").AddParameter("Identity", owner).AddParameter("ErrorAction", "SilentlyContinue");
                var results = ps.Invoke();
                if (results.Count > 0)
                {
                    rows.Add((results[0].Properties["DisplayName"]?.Value?.ToString() ?? string.Empty, results[0].Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? string.Empty, "Owner"));
                }
            }

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                if (isDynamic)
                {
                    // Dynamic groups need their members expanded via the group's recipient filter.
                    string? filter;
                    using (var filterPs = PowerShell.Create())
                    {
                        filterPs.Runspace = _runspace;
                        filterPs.AddCommand("Get-DynamicDistributionGroup").AddParameter("Identity", groupIdentity).AddParameter("ErrorAction", "Stop");
                        var ddgResults = filterPs.Invoke();
                        filter = ddgResults.Count > 0 ? ddgResults[0].Properties["RecipientFilter"]?.Value?.ToString() : null;
                    }

                    ps.AddCommand("Get-Recipient").AddParameter("RecipientPreviewFilter", filter).AddParameter("ResultSize", "Unlimited").AddParameter("ErrorAction", "Stop");
                }
                else
                {
                    ps.AddCommand("Get-DistributionGroupMember").AddParameter("Identity", groupIdentity).AddParameter("ResultSize", "Unlimited").AddParameter("ErrorAction", "Stop");
                }

                foreach (var r in ps.Invoke())
                {
                    rows.Add((r.Properties["DisplayName"]?.Value?.ToString() ?? string.Empty, r.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? string.Empty, "Member"));
                }
                if (ps.HadErrors) throw BuildPipelineException(ps);
            }

            return rows;
        });

        /// <summary>
        /// Enumerates all non-inherited, non-system Full Access delegates for a mailbox, ported from
        /// both reporting scripts' Get-MailboxPermission usage.
        /// </summary>
        public Task<List<string>> GetAllFullAccessDelegatesAsync(string mailboxIdentity) =>
            PermissionVerification.ReadAsync(() => GetAllFullAccessDelegatesCoreAsync(mailboxIdentity));

        private Task<List<string>> GetAllFullAccessDelegatesCoreAsync(string mailboxIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-MailboxPermission").AddParameter("Identity", mailboxIdentity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);

            return results
                .Where(r =>
                {
                    var accessRights = r.Properties["AccessRights"]?.Value as IEnumerable<object>;
                    var user = r.Properties["User"]?.Value?.ToString() ?? string.Empty;
                    return (accessRights?.Any(a => string.Equals(a?.ToString(), "FullAccess", StringComparison.OrdinalIgnoreCase)) ?? false)
                        && !user.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase);
                })
                .Select(r => r.Properties["User"]?.Value?.ToString() ?? string.Empty)
                .ToList();
        });

        /// <summary>
        /// Enumerates all Send As delegates for a mailbox, ported from both reporting scripts'
        /// Get-RecipientPermission usage.
        /// </summary>
        public Task<List<string>> GetAllSendAsDelegatesAsync(string mailboxIdentity) =>
            PermissionVerification.ReadAsync(() => GetAllSendAsDelegatesCoreAsync(mailboxIdentity));

        private Task<List<string>> GetAllSendAsDelegatesCoreAsync(string mailboxIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-RecipientPermission").AddParameter("Identity", mailboxIdentity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);

            return results
                .Where(r =>
                {
                    var accessRights = r.Properties["AccessRights"]?.Value as IEnumerable<object>;
                    var trustee = r.Properties["Trustee"]?.Value?.ToString() ?? string.Empty;
                    return (accessRights?.Any(a => string.Equals(a?.ToString(), "SendAs", StringComparison.OrdinalIgnoreCase)) ?? false)
                        && !trustee.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase);
                })
                .Select(r => r.Properties["Trustee"]?.Value?.ToString() ?? string.Empty)
                .ToList();
        });

        /// <summary>
        /// Enumerates Send on Behalf delegates for a mailbox, resolving each entry's DN/GUID to a
        /// friendly display value where possible, ported from GrantSendOnBehalfTo usage in both scripts.
        /// </summary>
        public Task<List<string>> GetAllSendOnBehalfDelegatesAsync(string mailboxIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Mailbox").AddParameter("Identity", mailboxIdentity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
            if (results.Count == 0) return new List<string>();

            var delegates = results[0].Properties["GrantSendOnBehalfTo"]?.Value as IEnumerable;
            if (delegates == null) return new List<string>();

            var resolved = new List<string>();
            foreach (var delegateObj in delegates)
            {
                var delegateStr = delegateObj?.ToString();
                if (string.IsNullOrEmpty(delegateStr)) continue;

                using var lookupPs = PowerShell.Create();
                lookupPs.Runspace = _runspace;
                lookupPs.AddCommand("Get-Recipient").AddParameter("Identity", delegateStr).AddParameter("ErrorAction", "SilentlyContinue");
                var lookupResults = lookupPs.Invoke();
                resolved.Add(lookupResults.Count > 0 ? (lookupResults[0].Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? delegateStr) : delegateStr);
            }
            return resolved;
        });

        // --- Shared Mailbox Creation ---

        /// <summary>
        /// Creates a new shared mailbox, ported from EXO-SharedMailbox.ps1's New-Mailbox call.
        /// </summary>
        public Task CreateSharedMailboxAsync(string displayName, string email) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("New-Mailbox")
              .AddParameter("Name", displayName)
              .AddParameter("DisplayName", displayName)
              .AddParameter("PrimarySmtpAddress", email)
              .AddParameter("Shared", true)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task<string?> GetMailboxRecipientTypeDetailsAsync(string identity, bool useExoCmdlet) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand(useExoCmdlet ? "Get-EXOMailbox" : "Get-Mailbox")
              .AddParameter("Identity", identity)
                            .AddParameter("ErrorAction", "Continue");
            var results = ps.Invoke();
            if (ps.HadErrors && ps.Streams.Error.Count > 0 && ps.Streams.Error.All(IsMissingObject)) return null;
            if (ps.HadErrors) throw BuildPipelineException(ps);
            if (results.Count == 0)
            {
                return null;
            }

            return results[0].Properties["RecipientTypeDetails"]?.Value?.ToString()
                ?? throw new InvalidOperationException("EXO returned a mailbox without its recipient type; state is unconfirmed.");
        });

        /// <summary>
        /// True only for the server's typed "object not found" error. REST-backed cmdlets such as Get-EXOMailbox surface it
        /// as a plain error whose message carries HttpStatusCode=404 and ManagementObjectNotFoundException.
        /// </summary>
        internal static bool IsMissingObject(ErrorRecord error)
        {
            if (error.CategoryInfo.Category == ErrorCategory.ObjectNotFound &&
                error.FullyQualifiedErrorId.Contains("ManagementObjectNotFoundException", StringComparison.Ordinal)) return true;

            foreach (var text in new[] { error.Exception?.Message, error.ErrorDetails?.Message, error.FullyQualifiedErrorId, error.ToString() })
            {
                if (text != null &&
                    text.Contains("HttpStatusCode=404", StringComparison.Ordinal) &&
                    text.Contains("ManagementObjectNotFoundException", StringComparison.Ordinal)) return true;
            }

            return false;
        }

        public Task<double> GetMailboxSizeMbAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-EXOMailboxStatistics")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors || results.Count == 0)
            {
                throw BuildPipelineException(ps);
            }

            var totalItemSize = results[0].Properties["TotalItemSize"]?.Value?.ToString() ?? string.Empty;
            var match = System.Text.RegularExpressions.Regex.Match(totalItemSize, @"\(([\d,]+)\s*bytes\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var raw = match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal);
                if (double.TryParse(raw, out var bytes))
                {
                    return Math.Round(bytes / 1024d / 1024d, 2);
                }
            }

            throw new InvalidOperationException("Mailbox size could not be read. Conversion stopped because the 50 GB limit cannot be verified.");
        });

        public Task ConvertMailboxToSharedAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("Type", "Shared")
              .AddParameter("Force")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task<bool> HasDirectFullAccessAsync(string mailboxIdentity, string userIdentity) =>
            PermissionVerification.ReadAsync(() => HasDirectFullAccessCoreAsync(mailboxIdentity, userIdentity));

        private Task<bool> HasDirectFullAccessCoreAsync(string mailboxIdentity, string userIdentity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-MailboxPermission")
              .AddParameter("Identity", mailboxIdentity)
              .AddParameter("User", userIdentity)
              .AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
            foreach (var r in results)
            {
                var inherited = r.Properties["IsInherited"]?.Value as bool? ?? false;
                var accessRights = r.Properties["AccessRights"]?.Value;
                var deny = r.Properties["Deny"]?.Value is bool denied && denied;
                if (!inherited && !deny && PermissionVerification.HasAccessRight(accessRights, "FullAccess"))
                {
                    return true;
                }
            }

            return false;
        });

        // --- Group Creation ---

        /// <summary>
        /// Creates a new Distribution Group (or mail-enabled Security Group) via New-DistributionGroup,
        /// optionally seeding initial members and owner in the same call, then applies communication
        /// (external sender) and joining/leaving restrictions via Set-DistributionGroup, matching the
        /// options exposed in the Exchange Admin Center's "Create group" wizard.
        /// </summary>
        public Task CreateDistributionGroupAsync(string displayName, string alias, string primarySmtpAddress,
            bool isSecurityGroup, string[]? members, string owner,
            bool allowExternalSenders, string memberJoinRestriction, string memberDepartRestriction) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("New-DistributionGroup")
              .AddParameter("Name", displayName)
              .AddParameter("DisplayName", displayName)
              .AddParameter("Alias", alias)
              .AddParameter("PrimarySmtpAddress", primarySmtpAddress)
              .AddParameter("Type", isSecurityGroup ? "Security" : "Distribution")
              .AddParameter("ManagedBy", owner)
              .AddParameter("MemberJoinRestriction", memberJoinRestriction)
              .AddParameter("MemberDepartRestriction", memberDepartRestriction)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            if (members != null && members.Length > 0)
            {
                ps.AddParameter("Members", members);
            }
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);

            using var psSet = PowerShell.Create();
            psSet.Runspace = _runspace;
            psSet.AddCommand("Set-DistributionGroup")
                 .AddParameter("Identity", primarySmtpAddress)
                 .AddParameter("RequireSenderAuthenticationEnabled", !allowExternalSenders)
                 .AddParameter("Confirm", false)
                 .AddParameter("ErrorAction", "Stop");
            psSet.Invoke();
            if (psSet.HadErrors) throw BuildPipelineException(psSet);
        });

        /// <summary>
        /// Creates a new Microsoft 365 (Unified) Group via New-UnifiedGroup, optionally seeding
        /// initial members/owner and setting the group's privacy (AccessType). Returns the group's
        /// AAD object id (ExternalDirectoryObjectId) so callers can subsequently provision a Team via
        /// Microsoft Graph if requested.
        /// </summary>
        public Task<string?> CreateUnifiedGroupAsync(string displayName, string alias, string primarySmtpAddress,
            bool isPrivate, string[]? members, string owner) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("New-UnifiedGroup")
              .AddParameter("DisplayName", displayName)
              .AddParameter("Alias", alias)
              .AddParameter("PrimarySmtpAddress", primarySmtpAddress)
              .AddParameter("AccessType", isPrivate ? "Private" : "Public")
              .AddParameter("Owner", owner)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            if (members != null && members.Length > 0)
            {
                ps.AddParameter("Members", members);
            }
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);

            return results.FirstOrDefault()?.Properties["ExternalDirectoryObjectId"]?.Value?.ToString();
        });

        public Task SetUserDepartmentAsync(string identity, string department) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-User")
              .AddParameter("Identity", identity)
              .AddParameter("Department", department)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task EnableMailboxArchiveAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Enable-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("Archive", true)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        public Task SetHiddenFromAddressListsAsync(string identity, bool hidden) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("HiddenFromAddressListsEnabled", hidden)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        /// <summary>
        /// Applies delivery restrictions (RequireSenderAuthenticationEnabled plus optional
        /// Accept/Reject sender lists) in a single Set-Mailbox call, ported from the script's
        /// $deliveryParams hashtable usage.
        /// </summary>
        public Task SetDeliveryRestrictionsAsync(string identity, bool requireSenderAuthentication, string[]? acceptedSenders, string[]? blockedSenders) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("RequireSenderAuthenticationEnabled", requireSenderAuthentication)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            if (acceptedSenders != null && acceptedSenders.Length > 0)
            {
                ps.AddParameter("AcceptMessagesOnlyFrom", acceptedSenders);
            }
            if (blockedSenders != null && blockedSenders.Length > 0)
            {
                ps.AddParameter("RejectMessagesFrom", blockedSenders);
            }
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        // --- Distribution Group Settings (Delivery management, Delegates, Message approval, Membership approvals) ---

        /// <summary>
        /// Reads the current Distribution Group / Mail-Enabled Security Group settings that back the
        /// EAC "Delivery management", "Message approval", and "Membership approvals" edit panels, via
        /// Get-DistributionGroup. Send As delegates and Send on Behalf delegates are read separately
        /// (see GetSendAsDelegatesAsync) since Send As comes from Get-RecipientPermission.
        /// </summary>
        public Task<DistributionGroupSettings?> GetDistributionGroupSettingsAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-DistributionGroup").AddParameter("Identity", identity).AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
            if (results.Count == 0) return null;

            var r = results[0];
            string?[] ToStringArray(string propName)
            {
                var val = r.Properties[propName]?.Value as IEnumerable<object>;
                return val?.Select(v => v?.ToString()).Where(v => !string.IsNullOrEmpty(v)).ToArray() ?? Array.Empty<string?>();
            }

            return new DistributionGroupSettings
            {
                RequireSenderAuthenticationEnabled = r.Properties["RequireSenderAuthenticationEnabled"]?.Value as bool? ?? true,
                AcceptMessagesOnlyFromSendersOrMembers = ToStringArray("AcceptMessagesOnlyFromSendersOrMembers")!,
                ModerationEnabled = r.Properties["ModerationEnabled"]?.Value as bool? ?? false,
                ModeratedBy = ToStringArray("ModeratedBy")!,
                BypassModerationFromSendersOrMembers = ToStringArray("BypassModerationFromSendersOrMembers")!,
                SendModerationNotifications = r.Properties["SendModerationNotifications"]?.Value?.ToString() ?? "Always",
                MemberJoinRestriction = r.Properties["MemberJoinRestriction"]?.Value?.ToString() ?? "Open",
                MemberDepartRestriction = r.Properties["MemberDepartRestriction"]?.Value?.ToString() ?? "Open",
                GrantSendOnBehalfTo = ToStringArray("GrantSendOnBehalfTo")!
            };
        });

        /// <summary>
        /// Applies "Delivery management" settings: whether external (outside-org) senders can email the
        /// group (RequireSenderAuthenticationEnabled is the inverse), and the optional "Specified senders"
        /// restriction list (AcceptMessagesOnlyFromSendersOrMembers). Passing an empty array clears the
        /// restriction so any allowed sender can send, matching the EAC panel's default "no restriction".
        /// </summary>
        public Task SetDeliveryManagementAsync(string identity, bool allowExternalSenders, string[] specifiedSenders) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-DistributionGroup")
              .AddParameter("Identity", identity)
              .AddParameter("RequireSenderAuthenticationEnabled", !allowExternalSenders)
              .AddParameter("AcceptMessagesOnlyFromSendersOrMembers", specifiedSenders)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        /// <summary>
        /// Returns the current Send As delegates for a group via Get-RecipientPermission, mirroring the
        /// EAC "Edit delegates" panel's delegate list (Send As portion).
        /// </summary>
        public Task<List<string>> GetSendAsDelegatesAsync(string identity) =>
            PermissionVerification.ReadAsync(() => GetSendAsDelegatesCoreAsync(identity));

        private Task<List<string>> GetSendAsDelegatesCoreAsync(string identity) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-RecipientPermission")
              .AddParameter("Identity", identity)
              .AddParameter("AccessRights", "SendAs")
                            .AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
            return results
                .Select(r => r.Properties["Trustee"]?.Value?.ToString())
                .Where(v => !string.IsNullOrEmpty(v) && !string.Equals(v, "NT AUTHORITY\\SELF", StringComparison.OrdinalIgnoreCase))
                .Select(v => v!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

            public Task AddSendAsDelegateAsync(string identity, string delegateIdentity) => AddSendAsAsync(identity, delegateIdentity);

            public Task RemoveSendAsDelegateAsync(string identity, string delegateIdentity) => RemoveSendAsAsync(identity, delegateIdentity);

        /// <summary>
        /// Sets the full Send on Behalf delegate list (GrantSendOnBehalfTo replaces the entire list on
        /// each call, matching Set-DistributionGroup's behavior), mirroring the "Send on Behalf" portion
        /// of the EAC "Edit delegates" panel.
        /// </summary>
        public Task SetSendOnBehalfDelegatesAsync(string identity, string[] delegates) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-DistributionGroup")
              .AddParameter("Identity", identity)
              .AddParameter("GrantSendOnBehalfTo", delegates)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        /// <summary>
        /// Applies "Message approval" (moderation) settings: whether messages sent to the group require
        /// moderator approval, the list of moderators, senders who bypass approval, and who is notified
        /// when a message isn't approved, matching the EAC "Edit message approval" panel.
        /// </summary>
        public Task SetMessageApprovalAsync(string identity, bool requireModeratorApproval, string[] moderators,
            string[] bypassSenders, string notifySenderMode) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-DistributionGroup")
              .AddParameter("Identity", identity)
              .AddParameter("ModerationEnabled", requireModeratorApproval)
              .AddParameter("ModeratedBy", moderators)
              .AddParameter("BypassModerationFromSendersOrMembers", bypassSenders)
              .AddParameter("SendModerationNotifications", notifySenderMode)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        /// <summary>
        /// Applies "Membership approvals" settings (MemberJoinRestriction / MemberDepartRestriction),
        /// the same properties set at group-creation time, matching the EAC "Edit membership approvals"
        /// panel's "Joining the group" / "Leaving the group" options.
        /// </summary>
        public Task SetMembershipApprovalAsync(string identity, string memberJoinRestriction, string memberDepartRestriction) => RunOnStaThreadAsync(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Set-DistributionGroup")
              .AddParameter("Identity", identity)
              .AddParameter("MemberJoinRestriction", memberJoinRestriction)
              .AddParameter("MemberDepartRestriction", memberDepartRestriction)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            if (ps.HadErrors) throw BuildPipelineException(ps);
        });

        // --- CSV / import helper (dialog handled in UI layer) ---

        private static RecipientInfo ToRecipientInfo(PSObject obj)
        {
            string? GetString(string name) => obj.Properties[name]?.Value?.ToString();
            return new RecipientInfo
            {
                Name = GetString("Name"),
                Alias = GetString("Alias"),
                DistinguishedName = GetString("DistinguishedName"),
                Guid = GetString("Guid"),
                PrimarySmtpAddress = GetString("PrimarySmtpAddress"),
                UserPrincipalName = GetString("UserPrincipalName"),
                Identity = GetString("Identity"),
                RecipientTypeDetails = GetString("RecipientTypeDetails"),
                RecipientType = GetString("RecipientType")
            };
        }

        private static Exception BuildPipelineException(PowerShell ps)
        {
            var message = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            return new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Unknown PowerShell pipeline error." : message);
        }

        private void EnsureRunspaceOpen()
        {
            if (_runspace != null && _runspace.RunspaceStateInfo.State == RunspaceState.Opened)
            {
                return;
            }

            // CreateDefault2() gives us the core cmdlets (Import-Module, etc.) needed to load
            // ExchangeOnlineManagement, but it also queues up PowerShell's built-in
            // *.format.ps1xml / *.types.ps1xml files for loading. Those files ship relative to a real
            // pwsh.exe install layout, and when the PowerShell SDK is hosted inside a WinUI app the paths
            // don't resolve, which caused "Errors occurred while loading the format data file".
            // Clearing the Formats/Types entries before opening the runspace skips that file loading
            // entirely while keeping the core cmdlets; ExchangeOnlineManagement loads its own formatting
            // data when imported, so the default set isn't needed here.
            var iss = InitialSessionState.CreateDefault2();
            iss.Formats.Clear();
            iss.Types.Clear();

            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;

            _runspace = RunspaceFactory.CreateRunspace(new LoggerPSHost(), iss);
            _runspace.ApartmentState = ApartmentState.STA;
            _runspace.ThreadOptions = PSThreadOptions.UseCurrentThread;
            _runspace.Open();
        }

        private void EnsureConsoleAllocated()
        {
            if (_consoleAllocated || GetConsoleWindow() != IntPtr.Zero)
            {
                _consoleAllocated = true;
                return;
            }

            if (AllocConsole())
            {
                var consoleWindow = GetConsoleWindow();
                if (consoleWindow != IntPtr.Zero)
                {
                    ShowWindow(consoleWindow, SW_HIDE);
                }

                var logWriter = new LoggerTextWriter();
                Console.SetOut(logWriter);
                Console.SetError(logWriter);
                _consoleAllocated = true;
            }
        }

        private void EnsureStaThreadStarted()
        {
            if (_staThread != null)
            {
                return;
            }

            _staThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ExoPowerShellWorker"
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

        private void WorkerLoop()
        {
            foreach (var action in _workQueue.GetConsumingEnumerable())
            {
                action();
            }
        }

        private Task<T> RunOnStaThreadAsync<T>(Func<T> work)
        {
            EnsureStaThreadStarted();
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _workQueue.Add(() =>
            {
                try
                {
                    tcs.SetResult(work());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private Task RunOnStaThreadAsync(Action work) => RunOnStaThreadAsync<object?>(() =>
        {
            work();
            return null;
        });

        private static void LogPipelineErrors(PowerShell ps, string context)
        {
            if (!ps.HadErrors)
            {
                return;
            }

            foreach (var error in ps.Streams.Error)
            {
                Logger.Log($"{context} error: {error}", LogType.Error);
            }
        }
    }

    /// <summary>
    /// Redirects Console.Out/Console.Error writes into the
    /// app's Logger, one line at a time, so the text appears in the UI's log panel instead of a
    /// separate console window. Ported from Entra Scout.
    /// </summary>
    public class LoggerTextWriter : System.IO.TextWriter
    {
        private readonly System.Text.StringBuilder _buffer = new();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();
            }
            else if (value != '\r')
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            foreach (var c in value)
            {
                Write(c);
            }
        }

        public override void Flush()
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            Logger.Log(_buffer.ToString());
            _buffer.Clear();
        }
    }
}
