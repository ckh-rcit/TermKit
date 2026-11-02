using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public enum GroupJoinRestriction
    {
        Open,
        Closed,
        ApprovalRequired
    }

    public enum GroupDepartRestriction
    {
        Open,
        Closed
    }

    public class GroupCreationRequest
    {
        public GroupKind GroupKind { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsSecurityGroup { get; set; }
        public bool IsPrivate { get; set; }
        public string[] Members { get; set; } = Array.Empty<string>();

        /// <summary>Every group must have at least one owner - Exchange/Graph do not allow ownerless groups.</summary>
        public string Owner { get; set; } = string.Empty;

        /// <summary>Microsoft 365 Groups only: provisions a Team on top of the group ("Create a team for this group").</summary>
        public bool CreateTeam { get; set; }

        /// <summary>Distribution/Mail-Enabled Security Groups only: allows external senders to email the group.</summary>
        public bool AllowExternalSenders { get; set; }

        /// <summary>Distribution Groups only: "Joining the group" policy.</summary>
        public GroupJoinRestriction JoinRestriction { get; set; } = GroupJoinRestriction.Open;

        /// <summary>Distribution Groups only: "Leaving the group" policy.</summary>
        public GroupDepartRestriction DepartRestriction { get; set; } = GroupDepartRestriction.Open;

        /// <summary>Mail-Enabled Security Groups only: "Require owner approval to join the group".</summary>
        public bool RequireOwnerApprovalToJoin { get; set; }
    }

    public class GroupCreationResult
    {
        public bool Success { get; set; }
        public List<string> Actions { get; } = new();
        public string TicketSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Orchestrates Distribution Group, Mail-Enabled Security Group, and Microsoft 365 (Unified) Group
    /// creation, mirroring the "Create Mailbox" section's request/result pattern. Both EXO-backed group
    /// types are created via Exchange Online cmdlets (New-DistributionGroup / New-UnifiedGroup), which
    /// is the Microsoft-documented path for provisioning these group types and keeps membership/ownership
    /// creation consistent with the existing EXO-backed GroupMembershipService for Distribution Groups.
    /// New-UnifiedGroup also provisions the underlying Microsoft 365 group object that
    /// GraphService/GroupMembershipService subsequently manage via Microsoft Graph. Every group requires
    /// at least one owner (Exchange/Graph reject ownerless groups), matching the admin center's "Create
    /// group" wizard, which also exposes the Teams/communication/joining/leaving/approval options ported
    /// here.
    /// </summary>
    public class GroupCreationService
    {
        private readonly ExoPowerShellService _exo;
        private readonly GraphService _graph;

        public GroupCreationService(ExoPowerShellService exo, GraphService graph)
        {
            _exo = exo;
            _graph = graph;
        }

        public async Task<GroupCreationResult> CreateGroupAsync(GroupCreationRequest request)
        {
            var result = new GroupCreationResult();
            var kindLabel = request.GroupKind == GroupKind.M365 ? "Microsoft 365 Group" : (request.IsSecurityGroup ? "Mail-Enabled Security Group" : "Distribution Group");

            if (string.IsNullOrWhiteSpace(request.Owner))
            {
                Logger.Log($"  ERROR: {kindLabel} '{request.Email}' requires at least one owner.", LogType.Error);
                result.Actions.Add("ERROR - Owner Required");
                result.Success = false;
                return result;
            }

            Logger.Log($"--- Starting {kindLabel} Creation ---");

            try
            {
                if (request.GroupKind == GroupKind.M365)
                {
                    Logger.Log($"Executing: New-UnifiedGroup -DisplayName '{request.DisplayName}' -Alias '{request.Alias}' -PrimarySmtpAddress '{request.Email}' -AccessType {(request.IsPrivate ? "Private" : "Public")} -Owner '{request.Owner}'");
                    var groupObjectId = await _exo.CreateUnifiedGroupAsync(request.DisplayName, request.Alias, request.Email, request.IsPrivate, request.Members, request.Owner);
                    result.Actions.Add($"{kindLabel} Created");
                    Logger.Log($"  SUCCESS: {kindLabel} '{request.Email}' created.");

                    if (request.CreateTeam)
                    {
                        if (string.IsNullOrEmpty(groupObjectId))
                        {
                            Logger.Log("  ERROR: Could not resolve the new group's object id; skipping Teams provisioning.", LogType.Error);
                            result.Actions.Add("ERROR Creating Team (Group Id Not Resolved)");
                        }
                        else
                        {
                            try
                            {
                                Logger.Log("Executing: New-Team (via Graph PUT /groups/{id}/team) to enable Microsoft Teams for this group.");
                                await _graph.CreateTeamFromGroupAsync(groupObjectId);
                                result.Actions.Add("Team Created");
                                Logger.Log("  SUCCESS: Microsoft Teams team created for this group.");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"  ERROR: Failed to create Team for '{request.Email}'. DETAILS: {ex.Message}", LogType.Error);
                                result.Actions.Add("ERROR Creating Team");
                            }
                        }
                    }
                }
                else
                {
                    var joinRestriction = request.JoinRestriction switch
                    {
                        GroupJoinRestriction.Closed => "Closed",
                        GroupJoinRestriction.ApprovalRequired => "ApprovalRequired",
                        _ => "Open"
                    };
                    var departRestriction = request.DepartRestriction == GroupDepartRestriction.Closed ? "Closed" : "Open";

                    // Mail-Enabled Security Groups only expose "require owner approval to join" (no open
                    // join / no self-leave options in the admin center), which maps to ApprovalRequired /
                    // Open vs Closed member join restriction, and members cannot self-remove either way.
                    if (request.IsSecurityGroup)
                    {
                        joinRestriction = request.RequireOwnerApprovalToJoin ? "ApprovalRequired" : "Open";
                        departRestriction = "Closed";
                    }

                    Logger.Log($"Executing: New-DistributionGroup -Name '{request.DisplayName}' -Alias '{request.Alias}' -PrimarySmtpAddress '{request.Email}' -Type {(request.IsSecurityGroup ? "Security" : "Distribution")} -ManagedBy '{request.Owner}'");
                    await _exo.CreateDistributionGroupAsync(request.DisplayName, request.Alias, request.Email, request.IsSecurityGroup, request.Members, request.Owner,
                        request.AllowExternalSenders, joinRestriction, departRestriction);

                    result.Actions.Add($"{kindLabel} Created");
                    Logger.Log($"  SUCCESS: {kindLabel} '{request.Email}' created.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"  ERROR: Failed to create {kindLabel} '{request.Email}'. DETAILS: {ex.Message}", LogType.Error);
                result.Actions.Add($"ERROR Creating {kindLabel}");
                result.Success = false;
                return result;
            }

            result.Success = true;
            result.TicketSummary = BuildTicketSummary(request, kindLabel);
            Logger.Log(result.TicketSummary, LogType.Ticket);
            Logger.Log($"--- {kindLabel} Creation Complete ---");
            return result;
        }

        private static string BuildTicketSummary(GroupCreationRequest request, string kindLabel)
        {
            var lines = new List<string>
            {
                "--- For IT Ticket ---",
                $"Group Type: {kindLabel}",
                $"Display Name: {request.DisplayName}",
                $"Email Address: {request.Email}",
                $"Owner: {request.Owner}"
            };

            if (request.Members.Length > 0)
            {
                lines.Add($"Initial Members: {string.Join(", ", request.Members)}");
            }

            if (request.GroupKind == GroupKind.M365)
            {
                lines.Add($"Privacy: {(request.IsPrivate ? "Private" : "Public")}");
                lines.Add($"Microsoft Teams: {(request.CreateTeam ? "Team Created" : "Not Created")}");
            }
            else
            {
                lines.Add($"Allow External Senders: {(request.AllowExternalSenders ? "Yes" : "No")}");
                if (request.IsSecurityGroup)
                {
                    lines.Add($"Require Owner Approval to Join: {(request.RequireOwnerApprovalToJoin ? "Yes" : "No")}");
                }
                else
                {
                    lines.Add($"Joining the Group: {request.JoinRestriction}");
                    lines.Add($"Leaving the Group: {request.DepartRestriction}");
                }
            }

            lines.Add("---------------------");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
