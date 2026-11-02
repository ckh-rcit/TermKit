using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TermKit.Services
{
    /// <summary>
    /// Result of attempting to restore one item from a snapshot.
    /// </summary>
    public class RestoreItemResult
    {
        public string User { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Replays a saved <see cref="SnapshotRecord"/> back through the existing EXO/Graph add-role and
    /// add-permission calls, effectively undoing a prior removal captured by <see cref="SnapshotService"/>.
    /// </summary>
    public class SnapshotRestoreService
    {
        private readonly ExoPowerShellService _exo;
        private readonly GraphService _graph;

        public SnapshotRestoreService(ExoPowerShellService exo, GraphService graph)
        {
            _exo = exo;
            _graph = graph;
        }

        public async Task<List<RestoreItemResult>> RestoreAsync(SnapshotRecord record)
        {
            var results = new List<RestoreItemResult>();

            switch (record.OperationType)
            {
                case "GroupMembershipRemoval":
                    await RestoreGroupMembershipAsync(record, results);
                    break;
                case "MailboxPermissionRemoval":
                    await RestoreMailboxPermissionsAsync(record, results);
                    break;
                default:
                    Logger.Log($"Snapshot type '{record.OperationType}' is not supported for restore.", LogType.Error);
                    break;
            }

            return results;
        }

        private async Task RestoreGroupMembershipAsync(SnapshotRecord record, List<RestoreItemResult> results)
        {
            var groupEmail = record.Target;
            var isM365 = record.Metadata.TryGetValue("GroupKind", out var kind) && string.Equals(kind, GroupKind.M365.ToString(), StringComparison.OrdinalIgnoreCase);
            record.Metadata.TryGetValue("M365GroupId", out var m365GroupId);

            foreach (var item in record.Items)
            {
                try
                {
                    if (isM365)
                    {
                        if (string.IsNullOrEmpty(m365GroupId) || string.IsNullOrEmpty(item.UserId))
                        {
                            throw new InvalidOperationException("Missing M365 group id or user id required to restore.");
                        }

                        if (string.Equals(item.Role, GroupRole.Member.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            await _graph.AddGroupMemberAsync(m365GroupId, item.UserId);
                        }
                        else
                        {
                            await _graph.AddGroupOwnerAsync(m365GroupId, item.UserId);
                        }
                    }
                    else
                    {
                        if (string.Equals(item.Role, GroupRole.Member.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            await _exo.AddDistributionGroupMemberAsync(groupEmail, item.User);
                        }
                        else
                        {
                            await _exo.AddDistributionGroupOwnerAsync(groupEmail, item.User);
                        }
                    }

                    Logger.Log($"Restored {item.Role} '{item.User}' to group '{groupEmail}'.", LogType.Success);
                    results.Add(new RestoreItemResult { User = item.User, Role = item.Role, Success = true, Message = "Restored" });
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to restore {item.Role} '{item.User}' to group '{groupEmail}'. DETAILS: {ex.Message}", LogType.Error);
                    results.Add(new RestoreItemResult { User = item.User, Role = item.Role, Success = false, Message = ex.Message });
                }
            }
        }

        private async Task RestoreMailboxPermissionsAsync(SnapshotRecord record, List<RestoreItemResult> results)
        {
            var targetIdentity = record.Target;

            foreach (var item in record.Items)
            {
                try
                {
                    switch (item.Role)
                    {
                        case "Full Access":
                            await _exo.AddFullAccessAsync(targetIdentity, item.User);
                            break;
                        case "Send As":
                            await _exo.AddSendAsAsync(targetIdentity, item.User);
                            break;
                        case "Send on Behalf":
                            await _exo.AddSendOnBehalfAsync(targetIdentity, item.User);
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown permission role '{item.Role}'.");
                    }

                    Logger.Log($"Restored {item.Role} for '{item.User}' on '{targetIdentity}'.", LogType.Success);
                    results.Add(new RestoreItemResult { User = item.User, Role = item.Role, Success = true, Message = "Restored" });
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to restore {item.Role} for '{item.User}' on '{targetIdentity}'. DETAILS: {ex.Message}", LogType.Error);
                    results.Add(new RestoreItemResult { User = item.User, Role = item.Role, Success = false, Message = ex.Message });
                }
            }
        }
    }
}
