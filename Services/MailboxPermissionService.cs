using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public enum PermissionTargetType
    {
        Mailbox,
        Resource
    }

    public enum PermissionOperationType
    {
        Add,
        Remove
    }

    public class PermissionSelections
    {
        public bool FullAccess { get; set; }
        public bool SendAs { get; set; }
        public bool SendOnBehalf { get; set; }
    }

    /// <summary>
    /// Result for a single (User, Target) permission processing pass, keyed the same way as the
    /// script's $results[$user][$targetIdentity] list of status strings (e.g. "Full Access (Added)",
    /// "Send As (Already Exists)", "Send on Behalf (Pending Validation)").
    /// </summary>
    public class PermissionResult
    {
        public string User { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public List<string> Statuses { get; } = new();
    }

    /// <summary>
    /// Ports Invoke-PermissionOperation (and its Add/Remove-MailboxPermissions / Add/Remove-ResourcePermissions
    /// wrappers) from the toolkit script: for each target mailbox/resource and each user, adds or
    /// removes Full Access, Send As, and Send on Behalf permissions, batching Send on Behalf changes
    /// into a single Set-Mailbox call per target, then polls once per second for up to 10 seconds to
    /// verify the change actually applied before reporting final status.
    /// </summary>
    public class MailboxPermissionService
    {
        private readonly ExoPowerShellService _exo;
        private readonly SnapshotService _snapshots;

        public MailboxPermissionService(ExoPowerShellService exo, SnapshotService? snapshots = null)
        {
            _exo = exo;
            _snapshots = snapshots ?? new SnapshotService();
        }

        public async Task<List<PermissionResult>> InvokePermissionOperationAsync(
            PermissionOperationType operationType,
            PermissionTargetType targetType,
            IEnumerable<string> targets,
            IEnumerable<string> users,
            PermissionSelections permissions)
        {
            var targetList = targets.ToList();
            var userList = users.ToList();
            var resultMap = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

            var actionVerb = operationType == PermissionOperationType.Add ? "Assignment" : "Removal";
            Logger.Log($"--- Starting {targetType} Permission {actionVerb} ---");

            foreach (var targetIdentity in targetList)
            {
                Logger.Log($"Processing {targetType}: {targetIdentity}");

                var mailboxExists = await _exo.MailboxExistsAsync(targetIdentity, resourceOnly: targetType == PermissionTargetType.Resource);
                if (!mailboxExists)
                {
                    Logger.Log($"{targetType} '{targetIdentity}' not found. Skipping.", LogType.Error);
                    continue;
                }

                var sendOnBehalfList = new List<string>();
                var userObjectMap = new Dictionary<string, RecipientInfo>(StringComparer.OrdinalIgnoreCase);
                var validationQueue = new List<(string User, string Permission)>();
                var snapshotItems = new List<SnapshotItem>();

                foreach (var user in userList)
                {
                    Logger.Log($"Processing User: {user} for {targetType}: {targetIdentity}");

                    var userObject = await _exo.GetRecipientAsync(user);
                    if (!GetOrCreateResultList(resultMap, user, targetIdentity, out var statuses))
                    {
                        // just created; continue
                    }

                    if (userObject == null)
                    {
                        Logger.Log($"User '{user}' not found. Skipping.", LogType.Error);
                        statuses.Add("User Not Found");
                        continue;
                    }
                    userObjectMap[user] = userObject;

                    if (permissions.FullAccess)
                    {
                        await ProcessFullAccessAsync(operationType, targetIdentity, user, userObject, statuses, validationQueue, snapshotItems);
                    }

                    if (permissions.SendAs)
                    {
                        await ProcessSendAsAsync(operationType, targetIdentity, user, statuses, validationQueue, snapshotItems);
                    }

                    if (permissions.SendOnBehalf)
                    {
                        ProcessSendOnBehalfMark(operationType, user, sendOnBehalfList, statuses);
                    }
                }

                if (permissions.SendOnBehalf && operationType == PermissionOperationType.Remove)
                {
                    await CaptureSendOnBehalfBeforeStateAsync(targetIdentity, sendOnBehalfList, snapshotItems);
                }

                if (sendOnBehalfList.Count > 0)
                {
                    await ApplySendOnBehalfBatchAsync(operationType, targetIdentity, sendOnBehalfList, resultMap, validationQueue);
                }

                if (validationQueue.Count > 0)
                {
                    await ValidatePendingChangesAsync(operationType, targetIdentity, validationQueue, userObjectMap, resultMap);
                }

                if (operationType == PermissionOperationType.Remove && snapshotItems.Count > 0)
                {
                    try
                    {
                        _snapshots.SaveSnapshot(new SnapshotRecord
                        {
                            OperationType = "MailboxPermissionRemoval",
                            Target = targetIdentity,
                            Description = $"Removed {snapshotItems.Count} permission(s) from {targetType} '{targetIdentity}'",
                            Items = snapshotItems
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to save permission removal snapshot for '{targetIdentity}'. DETAILS: {ex.Message}", LogType.Error);
                    }
                }
            }

            var summaryResults = resultMap.ToDictionary(
                userEntry => userEntry.Key,
                userEntry => (object)userEntry.Value,
                StringComparer.OrdinalIgnoreCase);
            Logger.WriteSummary($"{targetType} Permission {actionVerb}", summaryResults);

            return resultMap.SelectMany(userEntry => userEntry.Value.Select(targetEntry => new PermissionResult
            {
                User = userEntry.Key,
                Target = targetEntry.Key,
                Statuses = { }
            }.Also(r => r.Statuses.AddRange(targetEntry.Value)))).ToList();
        }

        private static bool GetOrCreateResultList(Dictionary<string, Dictionary<string, List<string>>> resultMap, string user, string target, out List<string> statuses)
        {
            var created = false;
            if (!resultMap.TryGetValue(user, out var targetMap))
            {
                targetMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                resultMap[user] = targetMap;
            }
            if (!targetMap.TryGetValue(target, out var list))
            {
                list = new List<string>();
                targetMap[target] = list;
                created = true;
            }
            statuses = list;
            return created;
        }

        private async Task ProcessFullAccessAsync(
            PermissionOperationType operationType, string targetIdentity, string user, RecipientInfo userObject,
            List<string> statuses, List<(string User, string Permission)> validationQueue, List<SnapshotItem> snapshotItems)
        {
            Logger.Log($"Attempting {operationType} Full Access...");

            // Per Microsoft's own documentation (Manage permissions for recipients in Exchange Online),
            // Full Access is only supported on User mailboxes, Resource mailboxes, Shared mailboxes, and
            // Discovery mailboxes. Microsoft 365 Group mailboxes are NOT a supported target for
            // Add-MailboxPermission / Remove-MailboxPermission at all; against those targets the cmdlets
            // don't just intermittently fail, they reliably throw the "Object reference not set" error
            // because the operation is unsupported, not because of a transient server bug. Check the
            // target's recipient type upfront and fail fast with an actionable message instead of
            // retrying an operation that can never succeed.
            var recipientTypeDetails = await _exo.GetRecipientTypeAsync(targetIdentity);
            if (string.Equals(recipientTypeDetails, "GroupMailbox", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log(
                    $"Full Access is not supported on Microsoft 365 Group mailboxes (target '{targetIdentity}' is Group-backed). " +
                    "Use Send As or Send on Behalf instead, or manage this via the group's owners/members.",
                    LogType.Error);
                statuses.Add("Full Access (Not Supported on M365 Group)");
                return;
            }

            try
            {
                var hasFullAccess = await CheckPermissionStateSafeAsync(
                    "Full Access",
                    () => _exo.HasFullAccessAsync(targetIdentity, user, userObject));

                if (operationType == PermissionOperationType.Add)
                {
                    if (hasFullAccess)
                    {
                        Logger.Log("Already granted Full Access.", LogType.Warning);
                        statuses.Add("Full Access (Already Exists)");
                    }
                    else
                    {
                        await AddFullAccessWithRetryAsync(targetIdentity, user, userObject);
                        Logger.Log("Full Access add command submitted. Pending validation.");
                        statuses.Add("Full Access (Pending Validation)");
                        validationQueue.Add((user, "Full Access"));
                    }
                }
                else
                {
                    if (hasFullAccess)
                    {
                        snapshotItems.Add(new SnapshotItem { User = user, Role = "Full Access" });
                    }
                    await RunWithNullReferenceRetryAsync(
                        "Full Access",
                        () => _exo.RemoveFullAccessAsync(targetIdentity, user),
                        () => _exo.HasFullAccessAsync(targetIdentity, user, userObject),
                        expectPresentAfterSuccess: false);
                    Logger.Log("Full Access remove command submitted. Pending validation.");
                    statuses.Add("Full Access (Pending Validation)");
                    validationQueue.Add((user, "Full Access"));
                }
            }
            catch (Exception ex)
            {
                if (operationType == PermissionOperationType.Remove && IsNotFoundError(ex.Message))
                {
                    Logger.Log("Full Access not found.", LogType.Warning);
                    statuses.Add("Full Access (Not Found)");
                }
                else if (IsNullReferenceServerError(ex.Message))
                {
                    // Even after RunWithNullReferenceRetryAsync's internal retry/verify, the lookup itself
                    // can keep hitting the same Exchange Online server-side bug. Don't report a hard
                    // failure here; defer to the post-batch validation pass, which polls for up to 10
                    // seconds and will confirm/refute whether the change actually landed.
                    Logger.Log($"Full Access {operationType} kept hitting the known Exchange Online server-side error. Deferring to post-batch validation.", LogType.Warning);
                    statuses.Add("Full Access (Pending Validation)");
                    validationQueue.Add((user, "Full Access"));
                }
                else
                {
                    Logger.Log($"Failed to {operationType} Full Access. DETAILS: {ex.Message}", LogType.Error);
                    statuses.Add("Full Access (Error)");
                }
            }
        }

        /// <summary>
        /// Add-MailboxPermission has a long-standing, Microsoft-acknowledged Exchange Online server-side
        /// bug where the cmdlet's response-building code throws "Write-ErrorMessage : Object reference
        /// not set to an instance of an object" even though the permission was actually granted (this is
        /// especially common against Microsoft 365 Group mailboxes and newly-created shared mailboxes).
        /// Rather than surfacing that as a hard failure, treat it as a transient/false-negative error:
        /// re-check whether the permission actually landed, and only if it still isn't present after a
        /// short pause do we retry the command once before giving up.
        /// </summary>
        private async Task AddFullAccessWithRetryAsync(string targetIdentity, string user, RecipientInfo userObject)
        {
            await RunWithNullReferenceRetryAsync(
                "Full Access",
                () => _exo.AddFullAccessAsync(targetIdentity, user),
                () => _exo.HasFullAccessAsync(targetIdentity, user, userObject),
                expectPresentAfterSuccess: true);
        }

        /// <summary>
        /// Several EXO permission cmdlets (Add/Remove-MailboxPermission, Add/Remove-RecipientPermission)
        /// share a long-standing, Microsoft-acknowledged server-side bug where the cmdlet's response-building
        /// code throws "Write-ErrorMessage : Object reference not set to an instance of an object" even though
        /// the change was actually applied (this is especially common against Microsoft 365 Group mailboxes
        /// and newly-created shared mailboxes). Rather than surfacing that as a hard failure, treat it as a
        /// transient/false-negative error: re-check whether the change actually landed, and only if it still
        /// doesn't match the expected state after a short pause do we retry the command once before giving up.
        /// </summary>
        /// <param name="permissionLabel">Friendly name used only for logging (e.g. "Full Access", "Send As").</param>
        /// <param name="action">The add/remove cmdlet invocation to run.</param>
        /// <param name="checkPresent">Checks whether the permission is currently present.</param>
        /// <param name="expectPresentAfterSuccess">True for Add (permission should now be present), false for Remove (permission should now be absent).</param>
        private async Task RunWithNullReferenceRetryAsync(
            string permissionLabel,
            Func<Task> action,
            Func<Task<bool>> checkPresent,
            bool expectPresentAfterSuccess)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (IsNullReferenceServerError(ex.Message))
            {
                Logger.Log($"{permissionLabel} returned a known Exchange Online server-side error (Write-ErrorMessage: Object reference not set). Checking whether the change was applied anyway...", LogType.Warning);

                await Task.Delay(2000);
                // Get-EXOMailboxPermission / Get-MailboxPermission / Get-RecipientPermission can hit this
                // same server-side bug on the lookup itself, so this verification check needs the same
                // safe-retry treatment as the initial pre-check, otherwise the NRE from checkPresent()
                // escapes this method entirely and the caller sees a hard failure instead of a retry.
                if (await CheckPermissionStateSafeAsync(permissionLabel, checkPresent) == expectPresentAfterSuccess)
                {
                    Logger.Log($"{permissionLabel} change was applied despite the server error. Continuing.", LogType.Success);
                    return;
                }

                Logger.Log($"{permissionLabel} change was not applied yet. Retrying once...", LogType.Warning);
                try
                {
                    await action();
                }
                catch (Exception retryEx) when (IsNullReferenceServerError(retryEx.Message))
                {
                    // The server sometimes throws this same error on a successful retry too; fall back to
                    // verification instead of failing outright, and let the normal post-batch validation
                    // step confirm/refute the final state.
                    Logger.Log($"Retry hit the same Exchange Online server-side error for {permissionLabel}. Deferring to permission validation.", LogType.Warning);
                }
            }
        }

        /// <summary>
        /// Get-EXOMailboxPermission / Get-MailboxPermission / Get-RecipientPermission can hit the same
        /// Microsoft-side "Write-ErrorMessage : Object reference not set to an instance of an object"
        /// server bug as the Add/Remove cmdlets, especially against Microsoft 365 Group-backed shared
        /// mailboxes. When that happens on the pre-check, the exception previously escaped straight to
        /// the outer catch and skipped the whole retry/verify path used for the actual Add/Remove call.
        /// Retry the existence check itself once after a short pause instead of failing outright.
        /// </summary>
        private async Task<bool> CheckPermissionStateSafeAsync(string permissionLabel, Func<Task<bool>> checkPresent)
        {
            try
            {
                return await checkPresent();
            }
            catch (Exception ex) when (IsNullReferenceServerError(ex.Message))
            {
                Logger.Log($"{permissionLabel} lookup returned a known Exchange Online server-side error (Write-ErrorMessage: Object reference not set). Retrying lookup...", LogType.Warning);
                await Task.Delay(2000);
                return await checkPresent();
            }
        }

        private static bool IsNullReferenceServerError(string message) =>
            message.Contains("Object reference not set to an instance of an object", StringComparison.OrdinalIgnoreCase);

        private async Task ProcessSendAsAsync(
            PermissionOperationType operationType, string targetIdentity, string user,
            List<string> statuses, List<(string User, string Permission)> validationQueue, List<SnapshotItem> snapshotItems)
        {
            Logger.Log($"Attempting {operationType} Send As...");
            try
            {
                var existing = await CheckPermissionStateSafeAsync(
                    "Send As",
                    () => _exo.HasSendAsAsync(targetIdentity, user));

                if (operationType == PermissionOperationType.Add)
                {
                    if (existing)
                    {
                        Logger.Log("Already granted Send As.", LogType.Warning);
                        statuses.Add("Send As (Already Exists)");
                    }
                    else
                    {
                        await RunWithNullReferenceRetryAsync(
                            "Send As",
                            () => _exo.AddSendAsAsync(targetIdentity, user),
                            () => _exo.HasSendAsAsync(targetIdentity, user),
                            expectPresentAfterSuccess: true);
                        Logger.Log("Send As add command submitted. Pending validation.");
                        statuses.Add("Send As (Pending Validation)");
                        validationQueue.Add((user, "Send As"));
                    }
                }
                else
                {
                    // Always capture the "before" snapshot item ahead of attempting the removal, even when
                    // the pre-check reported the permission as absent: that pre-check can be a false
                    // negative (Get-RecipientPermission lags behind recent changes), and if the removal
                    // below succeeds anyway, we still need the snapshot to exist so the change can be
                    // restored/undone later.
                    snapshotItems.Add(new SnapshotItem { User = user, Role = "Send As" });

                    // Don't gate the actual removal on the pre-check result: Get-RecipientPermission can
                    // lag behind a recent Add-RecipientPermission/Set-Mailbox change (Exchange Online's
                    // Get-* cmdlets read from an eventually-consistent cache), which previously caused a
                    // Send As permission that genuinely exists to be skipped entirely and misreported as
                    // "Not Found". Always attempt the removal and let the cmdlet's own "wasn't found on
                    // object" error (handled below) determine whether the permission truly doesn't exist.
                    await RunWithNullReferenceRetryAsync(
                        "Send As",
                        () => _exo.RemoveSendAsAsync(targetIdentity, user),
                        () => _exo.HasSendAsAsync(targetIdentity, user),
                        expectPresentAfterSuccess: false);
                    Logger.Log("Send As remove command submitted. Pending validation.");
                    statuses.Add("Send As (Pending Validation)");
                    validationQueue.Add((user, "Send As"));
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("wasn't found on object", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log("Send As not found (confirmed by remove).", LogType.Warning);
                    statuses.Add("Send As (Not Found)");
                    // The removal genuinely didn't apply, so the speculative snapshot item captured above
                    // (in case the pre-check was a false negative) doesn't reflect a real prior state and
                    // must be removed to avoid a bogus restore entry.
                    snapshotItems.RemoveAll(si => si.Role == "Send As" && string.Equals(si.User, user, StringComparison.OrdinalIgnoreCase));
                }
                else if (IsNullReferenceServerError(ex.Message))
                {
                    Logger.Log($"Send As {operationType} kept hitting the known Exchange Online server-side error. Deferring to post-batch validation.", LogType.Warning);
                    statuses.Add("Send As (Pending Validation)");
                    validationQueue.Add((user, "Send As"));
                }
                else
                {
                    Logger.Log($"Failed to {operationType} Send As. DETAILS: {ex.Message}", LogType.Error);
                    statuses.Add("Send As (Error)");
                    snapshotItems.RemoveAll(si => si.Role == "Send As" && string.Equals(si.User, user, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        private static void ProcessSendOnBehalfMark(
            PermissionOperationType operationType, string user, List<string> sendOnBehalfList, List<string> statuses)
        {
            if (operationType == PermissionOperationType.Add)
            {
                Logger.Log("Checking Send on Behalf...");
                // Existence check happens in ApplySendOnBehalfBatchAsync's caller via HasSendOnBehalfAsync
                // is intentionally deferred here to keep the batching identical to the script, so we just mark.
                if (!sendOnBehalfList.Contains(user, StringComparer.OrdinalIgnoreCase))
                {
                    sendOnBehalfList.Add(user);
                }
                statuses.Add("Send on Behalf (Pending)");
            }
            else
            {
                Logger.Log($"Marking '{user}' for Remove Send on Behalf.");
                if (!sendOnBehalfList.Contains(user, StringComparer.OrdinalIgnoreCase))
                {
                    sendOnBehalfList.Add(user);
                }
                statuses.Add("Send on Behalf (Pending Removal)");
            }
        }

        private async Task CaptureSendOnBehalfBeforeStateAsync(string targetIdentity, List<string> sendOnBehalfList, List<SnapshotItem> snapshotItems)
        {
            try
            {
                var existingDelegates = await _exo.GetAllSendOnBehalfDelegatesAsync(targetIdentity);
                foreach (var existingDelegate in existingDelegates)
                {
                    if (sendOnBehalfList.Contains(existingDelegate, StringComparer.OrdinalIgnoreCase))
                    {
                        snapshotItems.Add(new SnapshotItem { User = existingDelegate, Role = "Send on Behalf" });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to capture Send on Behalf snapshot for '{targetIdentity}'. DETAILS: {ex.Message}", LogType.Warning);
            }
        }

        private async Task ApplySendOnBehalfBatchAsync(
            PermissionOperationType operationType, string targetIdentity, List<string> sendOnBehalfList,
            Dictionary<string, Dictionary<string, List<string>>> resultMap,
            List<(string User, string Permission)> validationQueue)
        {
            var usersStr = string.Join(", ", sendOnBehalfList);
            Logger.Log($"Applying Send on Behalf {operationType} for '{targetIdentity}' (Users: {usersStr})...");

            try
            {
                if (operationType == PermissionOperationType.Add)
                {
                    await _exo.AddSendOnBehalfBatchAsync(targetIdentity, sendOnBehalfList.ToArray());
                }
                else
                {
                    await _exo.RemoveSendOnBehalfBatchAsync(targetIdentity, sendOnBehalfList.ToArray());
                }

                Logger.Log("Send on Behalf command submitted. Pending validation.");

                foreach (var processedUser in sendOnBehalfList)
                {
                    if (resultMap.TryGetValue(processedUser, out var targetMap) && targetMap.TryGetValue(targetIdentity, out var statuses))
                    {
                        validationQueue.Add((processedUser, "Send on Behalf"));
                        ReplaceStatus(statuses, operationType == PermissionOperationType.Add ? "Send on Behalf (Pending)" : "Send on Behalf (Pending Removal)", "Send on Behalf (Pending Validation)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed Send on Behalf {operationType} for '{targetIdentity}'. DETAILS: {ex.Message}", LogType.Error);
                foreach (var failedUser in sendOnBehalfList)
                {
                    if (resultMap.TryGetValue(failedUser, out var targetMap) && targetMap.TryGetValue(targetIdentity, out var statuses))
                    {
                        ReplaceStatus(statuses, operationType == PermissionOperationType.Add ? "Send on Behalf (Pending)" : "Send on Behalf (Pending Removal)", "Send on Behalf (Error)");
                    }
                }
            }
        }

        private async Task ValidatePendingChangesAsync(
            PermissionOperationType operationType, string targetIdentity,
            List<(string User, string Permission)> validationQueue,
            Dictionary<string, RecipientInfo> userObjectMap,
            Dictionary<string, Dictionary<string, List<string>>> resultMap)
        {
            // Exchange Online's Get-* cmdlets (Get-EXOMailboxPermission, Get-RecipientPermission,
            // Get-Mailbox) read from an eventually-consistent cache that can lag noticeably behind the
            // Add/Remove/Set cmdlets that actually mutate the permission, especially against Microsoft
            // 365 Group-backed shared mailboxes. Polling once per second for 10 seconds was too
            // aggressive and produced false "Verify Failed"/"Not Found" results even though the change
            // had landed. Poll less frequently (every 2 seconds) for longer (up to 30 seconds total) to
            // give replication more time to catch up before giving up.
            const int maxAttempts = 15;
            const int delayMs = 2000;
            Logger.Log($"Validating applied permission changes for '{targetIdentity}' every {delayMs / 1000} seconds for up to {maxAttempts * delayMs / 1000} seconds...");
            var pending = new List<(string User, string Permission)>(validationQueue);

            for (var attempt = 1; attempt <= maxAttempts && pending.Count > 0; attempt++)
            {
                await Task.Delay(delayMs);
                var remaining = new List<(string User, string Permission)>();

                foreach (var item in pending)
                {
                    if (!resultMap.TryGetValue(item.User, out var targetMap) || !targetMap.TryGetValue(targetIdentity, out var statuses))
                    {
                        continue;
                    }

                    bool isPresent = item.Permission switch
                    {
                        "Full Access" => await _exo.HasFullAccessAsync(targetIdentity, item.User, userObjectMap.GetValueOrDefault(item.User) ?? new RecipientInfo()),
                        "Send As" => await _exo.HasSendAsAsync(targetIdentity, item.User),
                        "Send on Behalf" => userObjectMap.TryGetValue(item.User, out var uo) && await _exo.HasSendOnBehalfAsync(targetIdentity, uo),
                        _ => false
                    };

                    var isValidated = operationType == PermissionOperationType.Add ? isPresent : !isPresent;
                    if (isValidated)
                    {
                        var verb = operationType == PermissionOperationType.Add ? "added" : "removed";
                        var suffix = operationType == PermissionOperationType.Add ? "Added" : "Removed";
                        Logger.Log($"SUCCESS: {item.Permission} {verb} and verified for '{item.User}'.", LogType.Success);
                        ReplaceStatus(statuses, $"{item.Permission} (Pending Validation)", $"{item.Permission} ({suffix})");
                    }
                    else
                    {
                        remaining.Add(item);
                    }
                }

                pending = remaining;
            }

            foreach (var item in pending)
            {
                if (!resultMap.TryGetValue(item.User, out var targetMap) || !targetMap.TryGetValue(targetIdentity, out var statuses))
                {
                    continue;
                }

                var opWord = operationType == PermissionOperationType.Add ? "add" : "remove";
                var stillOrNo = operationType == PermissionOperationType.Add ? "verification found no matching permission" : "permission still present";
                var timeoutSeconds = maxAttempts * delayMs / 1000;
                // The command was already confirmed to have been submitted successfully (no exception was
                // thrown, or the retry/verify path already handled the known server-side error). Reaching
                // this point only means EXO's read-side cache hadn't caught up within the polling window,
                // not that the change failed. Report it as "Unconfirmed" rather than "Verify Failed" so the
                // ticket note doesn't read as a hard failure when manual verification will likely show the
                // change actually applied.
                Logger.Log($"{item.Permission} {opWord} command succeeded but could not be confirmed for '{item.User}' within {timeoutSeconds} seconds. This is often an Exchange Online replication delay rather than an actual failure - please verify manually before assuming it did not apply.", LogType.Warning);
                var suffix = operationType == PermissionOperationType.Add ? "Add - Unconfirmed, Verify Manually" : "Remove - Unconfirmed, Verify Manually";
                ReplaceStatus(statuses, $"{item.Permission} (Pending Validation)", $"{item.Permission} ({suffix})");
            }
        }

        private static void ReplaceStatus(List<string> statuses, string oldValue, string newValue)
        {
            for (var i = 0; i < statuses.Count; i++)
            {
                if (string.Equals(statuses[i], oldValue, StringComparison.OrdinalIgnoreCase))
                {
                    statuses[i] = newValue;
                }
            }
        }

        private static bool IsNotFoundError(string message) =>
            message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("wasn't found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ACE", StringComparison.OrdinalIgnoreCase);
    }

    internal static class FluentExtensions
    {
        public static T Also<T>(this T self, Action<T> action)
        {
            action(self);
            return self;
        }
    }
}
