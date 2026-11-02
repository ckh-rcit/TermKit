using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TermKit.Services
{
    /// <summary>
    /// UI-facing snapshot of a Distribution Group's settings, mirroring the EAC "Delivery management",
    /// "Edit delegates", "Edit message approval", and "Edit membership approvals" panels.
    /// </summary>
    public class GroupSettingsSnapshot
    {
        // Delivery management
        public bool AllowExternalSenders { get; set; }
        public string[] SpecifiedSenders { get; set; } = Array.Empty<string>();

        // Manage delegates
        public string[] SendAsDelegates { get; set; } = Array.Empty<string>();
        public string[] SendOnBehalfDelegates { get; set; } = Array.Empty<string>();

        // Message approval
        public bool RequireModeratorApproval { get; set; }
        public string[] Moderators { get; set; } = Array.Empty<string>();
        public string[] BypassModerationSenders { get; set; } = Array.Empty<string>();
        public string NotifySenderMode { get; set; } = "Always";

        // Membership approvals
        public string JoinRestriction { get; set; } = "Open";
        public string DepartRestriction { get; set; } = "Open";
    }

    /// <summary>
    /// Loads and saves Distribution Group / Mail-Enabled Security Group settings for the "Group Settings"
    /// section, ported from the Exchange Admin Center's "Delivery management", "Edit delegates", "Edit
    /// message approval", and "Edit membership approvals" panels. Scoped to Distribution Groups only
    /// (Microsoft 365 Groups expose similar options through Microsoft Graph and are out of scope for now).
    /// </summary>
    public class GroupSettingsService
    {
        private static readonly string[] SupportedDistributionGroupTypes = { "MailUniversalDistributionGroup", "MailNonUniversalGroup" };

        private readonly ExoPowerShellService _exo;
        private readonly SnapshotService _snapshots;

        public GroupSettingsService(ExoPowerShellService exo, SnapshotService? snapshots = null)
        {
            _exo = exo;
            _snapshots = snapshots ?? new SnapshotService();
        }

        /// <summary>
        /// Confirms the identity resolves to a Distribution Group or Mail-Enabled Security Group before
        /// loading/saving settings, since Microsoft 365 Groups are not supported by these EXO cmdlets.
        /// </summary>
        public async Task<string?> ValidateDistributionGroupAsync(string identity)
        {
            var groupType = await _exo.GetDistributionGroupTypeAsync(identity);
            if (groupType == null)
            {
                Logger.Log($"  ERROR: Group '{identity}' not found.", LogType.Error);
                return null;
            }

            if (!SupportedDistributionGroupTypes.Contains(groupType, StringComparer.OrdinalIgnoreCase))
            {
                Logger.Log($"  ERROR: '{identity}' is a '{groupType}', not a Distribution Group or Mail-Enabled Security Group. Microsoft 365 Groups are not supported in this section.", LogType.Error);
                return null;
            }

            return groupType;
        }

        public async Task<GroupSettingsSnapshot?> LoadSettingsAsync(string identity)
        {
            var groupType = await ValidateDistributionGroupAsync(identity);
            if (groupType == null) return null;

            Logger.Log($"Loading group settings for '{identity}'...");
            var settings = await _exo.GetDistributionGroupSettingsAsync(identity);
            if (settings == null)
            {
                Logger.Log($"  ERROR: Could not read settings for '{identity}'.", LogType.Error);
                return null;
            }

            var sendAsDelegates = await _exo.GetSendAsDelegatesAsync(identity);

            var snapshot = new GroupSettingsSnapshot
            {
                AllowExternalSenders = !settings.RequireSenderAuthenticationEnabled,
                SpecifiedSenders = settings.AcceptMessagesOnlyFromSendersOrMembers,
                SendAsDelegates = sendAsDelegates.ToArray(),
                SendOnBehalfDelegates = settings.GrantSendOnBehalfTo,
                RequireModeratorApproval = settings.ModerationEnabled,
                Moderators = settings.ModeratedBy,
                BypassModerationSenders = settings.BypassModerationFromSendersOrMembers,
                NotifySenderMode = settings.SendModerationNotifications,
                JoinRestriction = settings.MemberJoinRestriction,
                DepartRestriction = settings.MemberDepartRestriction
            };

            Logger.Log($"  SUCCESS: Loaded settings for '{identity}'.", LogType.Success);
            return snapshot;
        }

        public async Task<bool> SaveDeliveryManagementAsync(string identity, bool allowExternalSenders, string[] specifiedSenders)
        {
            if (await ValidateDistributionGroupAsync(identity) == null) return false;

            try
            {
                Logger.Log($"Executing: Set-DistributionGroup -Identity '{identity}' -RequireSenderAuthenticationEnabled {!allowExternalSenders} -AcceptMessagesOnlyFromSendersOrMembers ...");
                await _exo.SetDeliveryManagementAsync(identity, allowExternalSenders, specifiedSenders);
                Logger.Log($"  SUCCESS: Delivery management settings updated for '{identity}'.", LogType.Success);

                WriteGroupSettingsTicketNote(identity, "Delivery Management", new List<string>
                {
                    $"Allow Senders Inside and Outside the Organization: {(allowExternalSenders ? "Yes" : "No")}",
                    $"Specified Senders: {(specifiedSenders.Length > 0 ? string.Join(", ", specifiedSenders) : "(none)")}"
                });

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"  ERROR: Failed to update delivery management settings for '{identity}'. DETAILS: {ex.Message}", LogType.Error);
                return false;
            }
        }

        public async Task<bool> SaveDelegatesAsync(string identity, string[] desiredSendAsDelegates, string[] desiredSendOnBehalfDelegates)
        {
            if (await ValidateDistributionGroupAsync(identity) == null) return false;

            try
            {
                var currentSendAs = await _exo.GetSendAsDelegatesAsync(identity);
                var toAddSendAs = desiredSendAsDelegates.Where(d => !currentSendAs.Contains(d, StringComparer.OrdinalIgnoreCase)).ToArray();
                var toRemoveSendAs = currentSendAs.Where(d => !desiredSendAsDelegates.Contains(d, StringComparer.OrdinalIgnoreCase)).ToArray();

                var currentSettings = await _exo.GetDistributionGroupSettingsAsync(identity);
                var currentSendOnBehalf = currentSettings?.GrantSendOnBehalfTo ?? Array.Empty<string>();
                var toRemoveSendOnBehalf = currentSendOnBehalf.Where(d => !desiredSendOnBehalfDelegates.Contains(d, StringComparer.OrdinalIgnoreCase)).ToArray();

                var snapshotItems = new List<SnapshotItem>();
                foreach (var d in toRemoveSendAs)
                {
                    snapshotItems.Add(new SnapshotItem { User = d, Role = "Send As" });
                }
                foreach (var d in toRemoveSendOnBehalf)
                {
                    snapshotItems.Add(new SnapshotItem { User = d, Role = "Send on Behalf" });
                }

                if (snapshotItems.Count > 0)
                {
                    try
                    {
                        _snapshots.SaveSnapshot(new SnapshotRecord
                        {
                            OperationType = "GroupDelegateRemoval",
                            Target = identity,
                            Description = $"Removed {snapshotItems.Count} delegate(s) from group '{identity}'",
                            Items = snapshotItems
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to save group delegate removal snapshot for '{identity}'. DETAILS: {ex.Message}", LogType.Error);
                    }
                }

                foreach (var d in toAddSendAs)
                {
                    Logger.Log($"Executing: Add-RecipientPermission -Identity '{identity}' -Trustee '{d}' -AccessRights SendAs");
                    await _exo.AddSendAsDelegateAsync(identity, d);
                }

                foreach (var d in toRemoveSendAs)
                {
                    Logger.Log($"Executing: Remove-RecipientPermission -Identity '{identity}' -Trustee '{d}' -AccessRights SendAs");
                    await _exo.RemoveSendAsDelegateAsync(identity, d);
                }

                Logger.Log($"Executing: Set-DistributionGroup -Identity '{identity}' -GrantSendOnBehalfTo ...");
                await _exo.SetSendOnBehalfDelegatesAsync(identity, desiredSendOnBehalfDelegates);

                Logger.Log($"  SUCCESS: Delegates updated for '{identity}'.", LogType.Success);

                WriteGroupSettingsTicketNote(identity, "Delegates", new List<string>
                {
                    $"Send As Added: {(toAddSendAs.Length > 0 ? string.Join(", ", toAddSendAs) : "(none)")}",
                    $"Send As Removed: {(toRemoveSendAs.Length > 0 ? string.Join(", ", toRemoveSendAs) : "(none)")}",
                    $"Send on Behalf: {(desiredSendOnBehalfDelegates.Length > 0 ? string.Join(", ", desiredSendOnBehalfDelegates) : "(none)")}",
                    $"Send on Behalf Removed: {(toRemoveSendOnBehalf.Length > 0 ? string.Join(", ", toRemoveSendOnBehalf) : "(none)")}"
                });

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"  ERROR: Failed to update delegates for '{identity}'. DETAILS: {ex.Message}", LogType.Error);
                return false;
            }
        }

        public async Task<bool> SaveMessageApprovalAsync(string identity, bool requireModeratorApproval, string[] moderators, string[] bypassSenders, string notifySenderMode)
        {
            if (await ValidateDistributionGroupAsync(identity) == null) return false;

            if (requireModeratorApproval && moderators.Length == 0)
            {
                Logger.Log("  ERROR: At least one moderator is required when moderator approval is enabled.", LogType.Error);
                return false;
            }

            try
            {
                Logger.Log($"Executing: Set-DistributionGroup -Identity '{identity}' -ModerationEnabled {requireModeratorApproval} -ModeratedBy ... -SendModerationNotifications {notifySenderMode}");
                await _exo.SetMessageApprovalAsync(identity, requireModeratorApproval, moderators, bypassSenders, notifySenderMode);
                Logger.Log($"  SUCCESS: Message approval settings updated for '{identity}'.", LogType.Success);

                WriteGroupSettingsTicketNote(identity, "Message Approval", new List<string>
                {
                    $"Require Moderator Approval: {(requireModeratorApproval ? "Yes" : "No")}",
                    $"Moderators: {(moderators.Length > 0 ? string.Join(", ", moderators) : "(none)")}",
                    $"Bypass Moderation Senders: {(bypassSenders.Length > 0 ? string.Join(", ", bypassSenders) : "(none)")}",
                    $"Notify Sender Mode: {notifySenderMode}"
                });

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"  ERROR: Failed to update message approval settings for '{identity}'. DETAILS: {ex.Message}", LogType.Error);
                return false;
            }
        }

        public async Task<bool> SaveMembershipApprovalAsync(string identity, string joinRestriction, string departRestriction)
        {
            if (await ValidateDistributionGroupAsync(identity) == null) return false;

            try
            {
                Logger.Log($"Executing: Set-DistributionGroup -Identity '{identity}' -MemberJoinRestriction {joinRestriction} -MemberDepartRestriction {departRestriction}");
                await _exo.SetMembershipApprovalAsync(identity, joinRestriction, departRestriction);
                Logger.Log($"  SUCCESS: Membership approval settings updated for '{identity}'.", LogType.Success);

                WriteGroupSettingsTicketNote(identity, "Membership Approval", new List<string>
                {
                    $"Join Restriction: {joinRestriction}",
                    $"Depart Restriction: {departRestriction}"
                });

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"  ERROR: Failed to update membership approval settings for '{identity}'. DETAILS: {ex.Message}", LogType.Error);
                return false;
            }
        }

        private static void WriteGroupSettingsTicketNote(string identity, string sectionLabel, List<string> details)
        {
            Logger.Log("--- For IT Ticket ---", LogType.Ticket);
            Logger.Log($"Group Settings Change: {sectionLabel}", LogType.Ticket);
            Logger.Log($"Group: {identity}", LogType.Ticket);
            foreach (var detail in details)
            {
                Logger.Log(detail, LogType.Ticket);
            }
            Logger.Log("---------------------", LogType.Ticket);
        }
    }
}
