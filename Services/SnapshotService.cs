using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermKit.Services
{
    /// <summary>
    /// A single restorable data point captured before a destructive action, e.g. one user who
    /// was a member/owner of a group, or one user who held a permission on a mailbox.
    /// </summary>
    public class SnapshotItem
    {
        /// <summary>Email/UPN or identity of the user affected.</summary>
        public string User { get; set; } = string.Empty;

        /// <summary>Directory object id (Graph) for the user, if resolved. Needed to restore M365 group membership/ownership.</summary>
        public string? UserId { get; set; }

        /// <summary>What role/permission this item represents, e.g. "Member", "Owner", "Full Access", "Send As", "Send on Behalf".</summary>
        public string Role { get; set; } = string.Empty;
    }

    /// <summary>
    /// A full record of the "before" state for one destructive operation batch (e.g. one group's
    /// member/owner removal, or one mailbox's permission removal), saved as JSON so it can be
    /// reviewed or replayed later to restore the removed access.
    /// </summary>
    public class SnapshotRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

        /// <summary>Short machine-friendly category, e.g. "GroupMembershipRemoval", "MailboxPermissionRemoval".</summary>
        public string OperationType { get; set; } = string.Empty;

        /// <summary>Human-readable summary shown in the UI list, e.g. "Removed 3 member(s)/owner(s) from group@contoso.com".</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>The group/mailbox/resource identity the items were removed from.</summary>
        public string Target { get; set; } = string.Empty;

        /// <summary>Extra context needed for restore that doesn't fit SnapshotItem, e.g. group kind or M365 group id.</summary>
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<SnapshotItem> Items { get; set; } = new();

        [JsonIgnore]
        public string? FilePath { get; set; }
    }

    /// <summary>
    /// Flattened, x:Bind-friendly projection of a <see cref="SnapshotRecord"/> for display in the Snapshots ListView.
    /// </summary>
    public class SnapshotListItem
    {
        public string TimestampDisplay { get; set; } = string.Empty;
        public string OperationType { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int ItemCount { get; set; }
        public SnapshotRecord Record { get; set; } = new();
    }

    /// <summary>
    /// Captures "before" state to timestamped JSON files under a Snapshots folder before destructive
    /// (removal) operations run, and lists/loads those files back so an admin can review or restore them.
    /// </summary>
    public class SnapshotService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _snapshotsDirectory;

        public SnapshotService(string? baseDirectory = null)
        {
            // MSIX-packaged installs run from a read-only location under Program Files\WindowsApps,
            // so snapshot files must live under the per-user LocalAppData folder instead.
            var resolvedBase = baseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermKit");
            _snapshotsDirectory = Path.Combine(resolvedBase, "Snapshots");
        }

        public string SnapshotsDirectory => _snapshotsDirectory;

        public string SaveSnapshot(SnapshotRecord record)
        {
            Directory.CreateDirectory(_snapshotsDirectory);

            var safeTarget = SanitizeForFileName(record.Target);
            var fileName = $"{record.Timestamp:yyyy-MM-dd_HHmmss}_{record.OperationType}_{safeTarget}_{record.Id[..8]}.json";
            var filePath = Path.Combine(_snapshotsDirectory, fileName);

            var json = JsonSerializer.Serialize(record, SerializerOptions);
            File.WriteAllText(filePath, json);

            record.FilePath = filePath;
            Logger.Log($"Snapshot saved: {fileName}");
            return filePath;
        }

        public List<SnapshotRecord> ListSnapshots()
        {
            if (!Directory.Exists(_snapshotsDirectory))
            {
                return new List<SnapshotRecord>();
            }

            var records = new List<SnapshotRecord>();
            foreach (var file in Directory.GetFiles(_snapshotsDirectory, "*.json").OrderByDescending(f => f))
            {
                try
                {
                    var record = LoadSnapshot(file);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to read snapshot '{file}': {ex.Message}", LogType.Warning);
                }
            }

            return records;
        }

        public List<SnapshotListItem> ListSnapshotItems()
        {
            return ListSnapshots().Select(r => new SnapshotListItem
            {
                TimestampDisplay = r.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                OperationType = r.OperationType,
                Description = r.Description,
                ItemCount = r.Items.Count,
                Record = r
            }).ToList();
        }

        public SnapshotRecord? LoadSnapshot(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = File.ReadAllText(filePath);
            var record = JsonSerializer.Deserialize<SnapshotRecord>(json, SerializerOptions);
            if (record != null)
            {
                record.FilePath = filePath;
            }
            return record;
        }

        private static string SanitizeForFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            var result = new string(chars);
            return result.Length > 60 ? result[..60] : result;
        }
    }
}
