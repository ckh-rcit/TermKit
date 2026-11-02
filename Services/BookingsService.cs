using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public class BookingsUserResult
    {
        public string User { get; set; } = string.Empty;
        public List<string> Actions { get; } = new();
    }

    /// <summary>
    /// Ports Enable-BookingsAccessGUI: resolves the Bookings license group (Settings.LicenseGroups.Bookings.GroupName),
    /// adds each requested user to that group via Graph (if not already a member), and sets their OWA
    /// mailbox policy to Settings.OwaPolicies.BookingsCreators (if not already set), reporting the same
    /// per-user action strings as the script (AAD User Found/Not Found, Already in Group, Added to Group,
    /// ERROR Adding to Group, Policy Already Set, Policy Set, ERROR Policy (Mbx Not Found), ERROR Setting Policy).
    /// </summary>
    public class BookingsService
    {
        private readonly ExoPowerShellService _exo;
        private readonly GraphService _graph;
        private readonly ToolConfig _config;

        public BookingsService(ExoPowerShellService exo, GraphService graph, ToolConfig config)
        {
            _exo = exo;
            _graph = graph;
            _config = config;
        }

        public async Task<Dictionary<string, List<string>>> EnableBookingsAccessAsync(IEnumerable<string> users)
        {
            var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var bookingsGroupName = _config.Settings.LicenseGroups.Bookings.GroupName;
            var bookingsOwaPolicy = _config.Settings.OwaPolicies.BookingsCreators;

            Logger.Log("--- Starting Bookings Access Enablement ---");

            Logger.Log($"Finding Bookings license group '{bookingsGroupName}'...");
            string? bookingsGroupId;
            try
            {
                bookingsGroupId = await _graph.GetGroupIdByNameAsync(bookingsGroupName);
                if (string.IsNullOrEmpty(bookingsGroupId))
                {
                    throw new InvalidOperationException($"Group '{bookingsGroupName}' not found.");
                }
                Logger.Log($"Found group: {bookingsGroupId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"ERROR: Bookings group '{bookingsGroupName}' not found. DETAILS: {ex.Message}", LogType.Error);
                return results;
            }

            foreach (var userUpn in users)
            {
                Logger.Log($"Processing User: {userUpn}");
                var userActions = new List<string>();
                results[userUpn] = userActions;

                string? azureUserId;
                try
                {
                    azureUserId = await _graph.GetUserIdAsync(userUpn);
                    if (string.IsNullOrEmpty(azureUserId))
                    {
                        throw new InvalidOperationException("User not found in Azure AD.");
                    }
                    Logger.Log($"  Found Azure AD User: {azureUserId}");
                    userActions.Add("AAD User Found");
                }
                catch (Exception ex)
                {
                    Logger.Log($"  ERROR: User '{userUpn}' not found (AAD). Skipping. DETAILS: {ex.Message}", LogType.Error);
                    userActions.Add("AAD User Not Found");
                    continue;
                }

                Logger.Log($"  Adding to group '{bookingsGroupName}'...");
                try
                {
                    var alreadyMember = await _graph.IsGroupMemberAsync(bookingsGroupId, azureUserId);
                    if (alreadyMember)
                    {
                        Logger.Log("    STATUS: Already in group.", LogType.Warning);
                        userActions.Add("Already in Group");
                    }
                    else
                    {
                        await _graph.AddGroupMemberAsync(bookingsGroupId, azureUserId);
                        Logger.Log("    STATUS: Added to group.", LogType.Success);
                        userActions.Add("Added to Group");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"    ERROR: Failed adding to group. DETAILS: {ex.Message}", LogType.Error);
                    userActions.Add("ERROR Adding to Group");
                }

                Logger.Log($"  Setting OWA policy to '{bookingsOwaPolicy}'...");
                try
                {
                    var currentPolicy = await _exo.GetOwaMailboxPolicyAsync(userUpn);
                    if (string.Equals(currentPolicy, bookingsOwaPolicy, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log("    STATUS: OWA policy already set.", LogType.Warning);
                        userActions.Add("Policy Already Set");
                    }
                    else
                    {
                        await _exo.SetOwaMailboxPolicyAsync(userUpn, bookingsOwaPolicy);
                        Logger.Log("    STATUS: Set OWA policy.", LogType.Success);
                        userActions.Add("Policy Set");
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"    ERROR: Mailbox '{userUpn}' not found (EXO). DETAILS: {ex.Message}", LogType.Error);
                        userActions.Add("ERROR Policy (Mbx Not Found)");
                    }
                    else
                    {
                        Logger.Log($"    ERROR: Failed setting OWA policy. DETAILS: {ex.Message}", LogType.Error);
                        userActions.Add("ERROR Setting Policy");
                    }
                }
            }

            Logger.Log("--- Bookings Access Enablement Completed ---");

            var summaryResults = results.ToDictionary(
                r => r.Key,
                r => (object)r.Value,
                StringComparer.OrdinalIgnoreCase);
            Logger.WriteSummary("Bookings Access", summaryResults);

            return results;
        }
    }
}
