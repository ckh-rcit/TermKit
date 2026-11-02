using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public sealed class RetentionConversionRequest
    {
        public string TaskNumber { get; set; } = string.Empty;
        public string TargetUserInput { get; set; } = string.Empty;
        public string DelegateInput { get; set; } = string.Empty;
        // The termination does not need the mailbox retained; the date is still stamped for the cleanup job and the delegate is optional.
        public bool NoRetention { get; set; }
        public string RetentionDateText { get; set; } = string.Empty;
    }

    public sealed class RetentionConversionResult
    {
        public bool Success { get; set; }
        public string SummaryText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Ports the AD disable + shared mailbox retention workflow from AD_UserTerm_RetainMail.ps1.
    /// </summary>
    public sealed class RetentionConversionService
    {
        private readonly OnPremAdService _ad;
        private readonly ExoPowerShellService _exo;
        private readonly GraphService _graph;
        private readonly ToolConfig _config;
        private readonly ServiceNowService? _serviceNow;

        public RetentionConversionService(OnPremAdService ad, ExoPowerShellService exo, GraphService graph, ToolConfig config, ServiceNowService? serviceNow)
        {
            _ad = ad;
            _exo = exo;
            _graph = graph;
            _config = config;
            _serviceNow = serviceNow;
        }

        public async Task<RetentionConversionResult> ProcessAsync(RetentionConversionRequest request, Func<string, Task<bool>> confirmAsync)
        {
            if (!_ad.IsConnected)
            {
                throw new InvalidOperationException("AD is not connected.");
            }
            if (!_exo.IsConnected)
            {
                throw new InvalidOperationException("EXO is not connected.");
            }

            var adConfig = _config.Settings.ActiveDirectory;
            if (adConfig == null)
            {
                throw new InvalidOperationException("ActiveDirectory settings are missing from config.");
            }

            ConfigService.ValidateRetention(_config);

            if (!DateTime.TryParseExact(request.RetentionDateText.Trim(), "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var retentionDate))
            {
                throw new InvalidOperationException("Retention Date must be in MM/DD/YYYY format.");
            }

            var targetInput = request.TargetUserInput.Trim();
            var delegateInput = request.DelegateInput.Trim();
            var taskNumber = request.TaskNumber.Trim();

            var hasDelegate = !string.IsNullOrWhiteSpace(delegateInput);
            if (string.IsNullOrWhiteSpace(targetInput) || string.IsNullOrWhiteSpace(taskNumber) || (!hasDelegate && !request.NoRetention))
            {
                throw new InvalidOperationException(request.NoRetention ? "Task # and Target User are required." : "Task #, Target User, and Delegate are required.");
            }

            var targetSam = ToSam(targetInput);
            var delegateSam = ToSam(delegateInput);
            var targetUpn = ToUpn(targetInput, adConfig.DomainDNS);
            var delegateUpn = ToUpn(delegateInput, adConfig.DomainDNS);
            var description = $"Shared Mailbox - DO NOT DELETE {taskNumber} - UNTIL {request.RetentionDateText.Trim()}";
            var disabledOu = adConfig.DisabledUsersOU;

            Logger.Log("--- Starting User Disable & Shared Mailbox Conversion Process ---");

            var adUser = await _ad.GetUserBySamAccountNameAsync(targetSam, adConfig);
            if (adUser == null)
            {
                if (await _ad.IsUserDeletedAsync(targetSam, adConfig))
                {
                    throw new InvalidOperationException($"Target user '{targetSam}' has been DELETED from AD. Please restore from AD Recycle Bin first.");
                }
                throw new InvalidOperationException($"Target user '{targetSam}' could not be found in Active Directory.");
            }

            if (!string.IsNullOrWhiteSpace(adUser.UserPrincipalName))
            {
                targetUpn = adUser.UserPrincipalName;
            }

            var delegateAdUpn = hasDelegate ? await _ad.GetUserUpnBySamAccountNameAsync(delegateSam, adConfig) : null;
            if (!string.IsNullOrWhiteSpace(delegateAdUpn))
            {
                delegateUpn = delegateAdUpn;
            }
            else if (hasDelegate && !delegateInput.Contains('@'))
            {
                Logger.Log($"WARNING: Delegate '{delegateSam}' UPN was not found in AD. Falling back to configured domain suffix '{adConfig.DomainDNS}'.", LogType.Warning);
            }

            if (adUser.Enabled)
            {
                throw new InvalidOperationException($"User '{targetSam}' is ENABLED in AD. Disable the account first, then retry.");
            }

            Logger.Log("[Pre-Check] AD user is confirmed disabled.", LogType.Success);

            var azureUser = await _graph.FindUserAsync(targetUpn, targetSam);
            GraphUserRecord? deletedUser = null;

            if (azureUser == null)
            {
                Logger.Log("[Pre-Check] Active user not found in Graph. Searching recycle bin...", LogType.Warning);
                var maxWaitSeconds = 300;
                for (var elapsed = 0; elapsed <= maxWaitSeconds; elapsed += 20)
                {
                    Logger.Log($"Processing: Searching Recycle Bin ({elapsed}s)...");
                    var deletedMatches = await _graph.FindDeletedUsersAsync(targetUpn, targetSam);
                    if (deletedMatches.Count > 1)
                    {
                        throw new InvalidOperationException($"Found {deletedMatches.Count} deleted Azure AD users matching '{targetSam}' (IDs: {string.Join(", ", deletedMatches.Select(u => u.Id))}). Restore the correct user manually, then run this tool again.");
                    }
                    deletedUser = deletedMatches.FirstOrDefault();
                    if (deletedUser != null)
                    {
                        Logger.Log("[Pre-Check] Found user in Azure AD recycle bin. Will be restored.", LogType.Success);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(20));
                }
            }

            var precheckMailboxType = await _exo.GetMailboxRecipientTypeDetailsAsync(targetUpn, useExoCmdlet: true);
            if (!string.IsNullOrWhiteSpace(precheckMailboxType))
            {
                Logger.Log($"[Pre-Check] Found existing mailbox. Type: {precheckMailboxType}.", LogType.Success);
            }
            else
            {
                Logger.Log("[Pre-Check] No existing mailbox found. Will wait for provisioning.");
            }

            Logger.Log("Processing: On-Prem AD State...");
            if (!string.IsNullOrWhiteSpace(adUser.DistinguishedName) &&
                adUser.DistinguishedName.IndexOf(disabledOu, StringComparison.OrdinalIgnoreCase) < 0)
            {
                Logger.Log("[AD] User is not in Disabled Users OU. Moving now...");
                await _ad.MoveUserToDisabledOuAsync(adUser.DistinguishedName, disabledOu, adConfig);
            }
            else
            {
                Logger.Log("[AD] User is already in Disabled Users OU. Skipping move.");
            }

            if (!string.Equals(adUser.Description, description, StringComparison.Ordinal))
            {
                Logger.Log("[AD] Updating user description...");
                await _ad.SetUserDescriptionAsync(targetSam, description, adConfig);
            }
            else
            {
                Logger.Log("[AD] User description is already set correctly. Skipping.");
            }

            await _ad.SetUserExpirationAsync(targetSam, retentionDate, adConfig);
            Logger.Log("[AD] AD properties updated successfully.", LogType.Success);

            Logger.Log("Processing: Azure AD State...");
            if (deletedUser != null)
            {
                Logger.Log("[Azure AD] Restoring user from recycle bin...", LogType.Warning);
                await _graph.RestoreDeletedUserAsync(deletedUser.Id);
                Logger.Log("[Azure AD] Restore command issued. Waiting 30s for object to become active...", LogType.Success);
                await Task.Delay(TimeSpan.FromSeconds(30));
            }

            azureUser = azureUser ?? await _graph.FindUserAsync(targetUpn, targetSam);
            if (azureUser == null)
            {
                throw new InvalidOperationException("Could not find or restore the user in Azure AD.");
            }

            var licensingGroupIds = _config.Settings.LicenseGroups.GetRetentionLicensingGroupIds().Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var primaryGroupId = _config.Settings.LicenseGroups.GetPrimaryRetentionGroupId();

            if (!string.Equals(precheckMailboxType, "SharedMailbox", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("[Azure AD] Ensuring membership in a licensing group...");
                var userGroupIds = await _graph.GetUserMemberGroupIdsAsync(azureUser.Id);
                var isLicensedByGroup = licensingGroupIds.Any(id => userGroupIds.Contains(id));
                if (!isLicensedByGroup)
                {
                    if (string.IsNullOrWhiteSpace(primaryGroupId))
                    {
                        throw new InvalidOperationException("Primary licensing group ID is not configured.");
                    }

                    Logger.Log("[Azure AD] User not in a licensing group. Adding to primary group...");
                    await _graph.AddGroupMemberAsync(primaryGroupId, azureUser.Id);
                    Logger.Log("[Azure AD] Added user to licensing group. Waiting 60s...", LogType.Success);
                    await Task.Delay(TimeSpan.FromSeconds(60));
                }
                else
                {
                    Logger.Log("[Azure AD] User is already a member of a known licensing group.");
                }
            }
            else
            {
                Logger.Log("[Azure AD] Mailbox already SharedMailbox. Skipping license assignment.");
            }

            var mailboxType = precheckMailboxType;
            if (string.IsNullOrWhiteSpace(mailboxType))
            {
                Logger.Log($"[EXO] Waiting for mailbox for '{targetUpn}' to provision...");
                await Task.Delay(TimeSpan.FromSeconds(30));
                var maxWaitSeconds = 900;
                for (var elapsed = 30; elapsed <= maxWaitSeconds; elapsed += 20)
                {
                    Logger.Log($"Processing: Waiting for Mailbox Provisioning ({elapsed}s)...");
                    mailboxType = await _exo.GetMailboxRecipientTypeDetailsAsync(targetUpn, useExoCmdlet: true);
                    if (!string.IsNullOrWhiteSpace(mailboxType))
                    {
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(20));
                }

                if (string.IsNullOrWhiteSpace(mailboxType))
                {
                    throw new InvalidOperationException($"Mailbox for '{targetUpn}' did not provision within the wait window.");
                }
            }

            Logger.Log($"[EXO] Mailbox is available. Type: {mailboxType}.", LogType.Success);

            var mailboxSizeMb = await _exo.GetMailboxSizeMbAsync(targetUpn);
            var oversizedMailboxProceeded = false;
            if (mailboxSizeMb > 50000)
            {
                var proceed = await confirmAsync($"Mailbox size is {mailboxSizeMb:N2} MB (> 50 GB). If you proceed, it will become read-only and cannot receive new mail. Continue?");
                if (!proceed)
                {
                    throw new InvalidOperationException("Process stopped by user due to mailbox size.");
                }
                oversizedMailboxProceeded = true;
            }

            var changedHybrid = await _ad.EnsureHybridSharedMailboxAttributesAsync(targetSam, adConfig);
            if (changedHybrid)
            {
                Logger.Log("[AD] Hybrid attributes set. Waiting 60s for AD sync...", LogType.Success);
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
            else
            {
                Logger.Log("[AD] Hybrid Exchange attributes already correct. Skipping.");
            }

            if (string.Equals(mailboxType, "UserMailbox", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"[EXO] Converting mailbox '{targetUpn}' to Shared...");
                await _exo.ConvertMailboxToSharedAsync(targetUpn);
                Logger.Log("[EXO] Set-Mailbox -Type Shared command issued.", LogType.Success);
            }
            else if (string.Equals(mailboxType, "SharedMailbox", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("[EXO] Mailbox already SharedMailbox. Skipping conversion.");
            }
            else
            {
                throw new InvalidOperationException($"Unexpected mailbox type '{mailboxType}'.");
            }

            const int convertWaitSeconds = 900;
            var confirmedShared = false;
            for (var elapsed = 0; elapsed <= convertWaitSeconds; elapsed += 15)
            {
                Logger.Log($"Processing: Waiting for SharedMailbox Confirmation ({elapsed}s)...");
                var currentType = await _exo.GetMailboxRecipientTypeDetailsAsync(targetUpn, useExoCmdlet: false);
                if (string.Equals(currentType, "SharedMailbox", StringComparison.OrdinalIgnoreCase))
                {
                    confirmedShared = true;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(15));
            }

            if (!confirmedShared)
            {
                throw new InvalidOperationException("Mailbox did not confirm as SharedMailbox within wait window.");
            }

            Logger.Log("[EXO] Mailbox confirmed as SharedMailbox type.", LogType.Success);
            Logger.Log("[Propagation] Starting 10 minute monitored wait for backend propagation...");

            var anomalyDetected = false;
            var checksPerformed = 0;
            const int propagationWaitSeconds = 600;
            const int checkIntervalSeconds = 30;

            for (var elapsed = 0; elapsed < propagationWaitSeconds; elapsed += checkIntervalSeconds)
            {
                Logger.Log($"Processing: Propagation Wait with Monitoring ({elapsed}s / {propagationWaitSeconds}s)...");

                if (elapsed > 0)
                {
                    checksPerformed++;
                    var currentType = await _exo.GetMailboxRecipientTypeDetailsAsync(targetUpn, useExoCmdlet: false);
                    if (!string.Equals(currentType, "SharedMailbox", StringComparison.OrdinalIgnoreCase))
                    {
                        anomalyDetected = true;
                        Logger.Log($"[Propagation] WARNING: Mailbox type changed to '{currentType}'. Re-converting...", LogType.Warning);
                        await _exo.ConvertMailboxToSharedAsync(targetUpn);
                    }
                    else
                    {
                        Logger.Log($"[Propagation] Check {checksPerformed}/{propagationWaitSeconds / checkIntervalSeconds}: type is SharedMailbox.");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds));
            }

            if (anomalyDetected)
            {
                var finalType = await _exo.GetMailboxRecipientTypeDetailsAsync(targetUpn, useExoCmdlet: false);
                if (!string.Equals(finalType, "SharedMailbox", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Mailbox type instability detected. Final type: {finalType}.");
                }
                Logger.Log("[Propagation] Final verification passed. Mailbox is stable as SharedMailbox.", LogType.Success);
            }
            else
            {
                Logger.Log($"[Propagation] All {checksPerformed} stability checks passed.", LogType.Success);
            }

            if (hasDelegate)
            {
                Logger.Log($"[EXO] Checking delegate FullAccess for '{delegateUpn}'...");
                var delegateHasAccess = await _exo.HasDirectFullAccessAsync(targetUpn, delegateUpn);
                if (!delegateHasAccess)
                {
                    await _exo.AddFullAccessAsync(targetUpn, delegateUpn);
                    Logger.Log($"[EXO] Granted FullAccess to '{delegateUpn}'.", LogType.Success);
                }
                else
                {
                    Logger.Log($"[EXO] Delegate '{delegateUpn}' already has FullAccess.");
                }
            }
            else
            {
                Logger.Log("[EXO] No delegate requested. Skipping FullAccess.");
            }

            Logger.Log("[Azure AD] Checking for licensing group membership before removal...");
            var memberGroups = await _graph.GetUserMemberGroupIdsAsync(azureUser.Id);
            var groupsToRemove = licensingGroupIds.Where(id => memberGroups.Contains(id)).ToArray();
            foreach (var groupId in groupsToRemove)
            {
                Logger.Log($"[Azure AD] Removing user from licensing group '{groupId}'...");
                await _graph.RemoveGroupMemberAsync(groupId, azureUser.Id);
            }

            if (groupsToRemove.Length > 0)
            {
                Logger.Log("[Azure AD] Waiting 30s for group membership removal to process...");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
            else
            {
                Logger.Log("[Azure AD] User is not a member of known licensing groups. Skipping removal.");
            }

            Logger.Log($"[Azure AD] Resetting password and disabling sign-in for '{targetUpn}'...");
            await _graph.ResetPasswordAndDisableSignInAsync(azureUser.Id, RetentionRules.NewRandomPassword());
            Logger.Log("[Azure AD] Successfully reset password and disabled sign-in.", LogType.Success);

            var summaryLines = new List<string>
            {
                "AD Account: Moved, Description/Expiration set.",
                $"Mailbox: Converted to Shared Mailbox ({targetUpn}).",
                "AD Hybrid Attributes: Set for stability.",
                hasDelegate ? $"Delegation: Full Access granted to '{delegateUpn}'." : "Delegation: None requested.",
                "Licensing: Removed.",
                "Sign-in: Disabled in Azure AD, password reset.",
                request.NoRetention
                    ? $"Retention set to {request.RetentionDateText.Trim()} (no email retention requested)."
                    : $"Retention set to {request.RetentionDateText.Trim()}."
            };

            if (oversizedMailboxProceeded)
            {
                summaryLines.Add($"WARNING: Mailbox size ({mailboxSizeMb:N2} MB) is over 50GB and now read-only.");
            }

            var summaryText = string.Join(Environment.NewLine, summaryLines);
            Logger.Log("Summary:", LogType.Summary);
            foreach (var line in summaryLines)
            {
                Logger.Log(line, LogType.Summary);
            }

            if (_serviceNow != null && !string.IsNullOrWhiteSpace(taskNumber))
            {
                var snResult = await _serviceNow.CloseTaskAsync(taskNumber, summaryText, summaryText);
                Logger.Log(snResult.Success
                    ? $"[ServiceNow] Ticket close/update succeeded. {snResult.Message}"
                    : $"[ServiceNow] Ticket close/update failed. {snResult.Message}",
                    snResult.Success ? LogType.Success : LogType.Warning);
            }

            Logger.Log("PROCESS COMPLETED SUCCESSFULLY.", LogType.Success);

            return new RetentionConversionResult
            {
                Success = true,
                SummaryText = summaryText
            };
        }

        private static string ToSam(string identity)
        {
            var trimmed = identity.Trim();
            var atIndex = trimmed.IndexOf('@');
            return atIndex > 0 ? trimmed[..atIndex] : trimmed;
        }

        private static string ToUpn(string identity, string domainDns)
        {
            var trimmed = identity.Trim();
            return trimmed.Contains('@') ? trimmed : $"{trimmed}@{domainDns}";
        }
    }
}
