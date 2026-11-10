using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermKit.Services
{
    public class LicenseGroupConfig
    {
        [JsonPropertyName("GroupName")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("GroupId")]
        public string? GroupId { get; set; }
    }

    public class LicenseGroupsConfig
    {
        [JsonPropertyName("Bookings")]
        public LicenseGroupConfig Bookings { get; set; } = new();

        [JsonPropertyName("M365_G3")]
        public LicenseGroupConfig? M365G3 { get; set; }

        [JsonPropertyName("M365_G5")]
        public LicenseGroupConfig? M365G5 { get; set; }

        [JsonPropertyName("PrimaryLicenseGroup")]
        public string? PrimaryLicenseGroup { get; set; }

        public string[] GetRetentionLicensingGroupIds()
        {
            var ids = new List<string>();
            if (!string.IsNullOrWhiteSpace(M365G3?.GroupId)) ids.Add(M365G3.GroupId!);
            if (!string.IsNullOrWhiteSpace(M365G5?.GroupId)) ids.Add(M365G5.GroupId!);
            return ids.ToArray();
        }

        public string? GetPrimaryRetentionGroupId()
        {
            var primary = (PrimaryLicenseGroup ?? string.Empty).Trim();
            if (primary.Equals("M365_G3", StringComparison.OrdinalIgnoreCase)) return M365G3?.GroupId;
            if (primary.Equals("M365_G5", StringComparison.OrdinalIgnoreCase)) return M365G5?.GroupId;
            return null;
        }
    }

    public class ActiveDirectoryConfig
    {
        [JsonPropertyName("DomainNetbios")]
        public string DomainNetbios { get; set; } = string.Empty;

        [JsonPropertyName("DomainDNS")]
        public string DomainDNS { get; set; } = string.Empty;

        [JsonPropertyName("PrimaryDomainController")]
        public string PrimaryDomainController { get; set; } = string.Empty;

        [JsonPropertyName("FallbackDomainControllers")]
        public List<string>? FallbackDomainControllers { get; set; }

        [JsonPropertyName("DisabledUsersOU")]
        public string DisabledUsersOU { get; set; } = string.Empty;
    }

    public class OwaPoliciesConfig
    {
        [JsonPropertyName("BookingsCreators")]
        public string BookingsCreators { get; set; } = string.Empty;
    }

    public class GraphApiConfig
    {
        [JsonPropertyName("Scopes")]
        public List<string> Scopes { get; set; } = new();
    }

    public class ServiceNowConfig
    {
        [JsonPropertyName("Enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("InstanceUrl")]
        public string InstanceUrl { get; set; } = string.Empty;

        [JsonPropertyName("KeyVaultUrl")]
        public string KeyVaultUrl { get; set; } = string.Empty;

        [JsonPropertyName("SubscriptionId")]
        public string SubscriptionId { get; set; } = string.Empty;

        [JsonPropertyName("UsernameSecretName")]
        public string UsernameSecretName { get; set; } = string.Empty;

        [JsonPropertyName("PasswordSecretName")]
        public string PasswordSecretName { get; set; } = string.Empty;

        [JsonPropertyName("Table")]
        public string Table { get; set; } = "task";

        [JsonPropertyName("TicketNumberField")]
        public string TicketNumberField { get; set; } = "number";

        [JsonPropertyName("CloseStateField")]
        public string CloseStateField { get; set; } = "state";

        [JsonPropertyName("CloseStateValue")]
        public string CloseStateValue { get; set; } = "3";

        [JsonPropertyName("WorkNotesField")]
        public string WorkNotesField { get; set; } = "work_notes";

        [JsonPropertyName("AdditionalCommentsField")]
        public string AdditionalCommentsField { get; set; } = "comments";

        [JsonPropertyName("UpdateTable")]
        public string UpdateTable { get; set; } = string.Empty;

        [JsonPropertyName("AdditionalFields")]
        public Dictionary<string, object>? AdditionalFields { get; set; }

        // Catalog variable names read from the task's requested item to pre-fill the retention form.
        [JsonPropertyName("RetentionTermUserVariable")]
        public string RetentionTermUserVariable { get; set; } = "v_employee_email";

        [JsonPropertyName("RetentionDelegateVariable")]
        public string RetentionDelegateVariable { get; set; } = "v_retention_user";

        [JsonPropertyName("RetentionEndDateVariable")]
        public string RetentionEndDateVariable { get; set; } = "v_inbox_deletion_date";

        [JsonPropertyName("RetentionFlagVariable")]
        public string RetentionFlagVariable { get; set; } = "v_retention";
    }

    public class SettingsConfig
    {
        [JsonPropertyName("ActiveDirectory")]
        public ActiveDirectoryConfig? ActiveDirectory { get; set; }

        [JsonPropertyName("LicenseGroups")]
        public LicenseGroupsConfig LicenseGroups { get; set; } = new();

        [JsonPropertyName("OwaPolicies")]
        public OwaPoliciesConfig OwaPolicies { get; set; } = new();

        [JsonPropertyName("GraphApi")]
        public GraphApiConfig GraphApi { get; set; } = new();

        [JsonPropertyName("ServiceNow")]
        public ServiceNowConfig? ServiceNow { get; set; }

        // HTTPS .appinstaller feed used by the in-app update check; stamped into the packaged sample at release time.
        [JsonPropertyName("UpdateFeedUrl")]
        public string UpdateFeedUrl { get; set; } = string.Empty;
    }

    public class ToolConfig
    {
        [JsonPropertyName("Settings")]
        public SettingsConfig Settings { get; set; } = new();
    }

    /// <summary>
    /// Loads and validates config.json, ported from Test-ConfigStructure. Required fields for the
    /// ported (non-Provisioning, non-Calendar) tabs are LicenseGroups.Bookings.GroupName,
    /// OwaPolicies.BookingsCreators, and a non-empty GraphApi.Scopes array. ServiceNow is optional
    /// (disabled ticket auto-close is a valid configuration).
    /// </summary>
    public static class ConfigService
    {
        // Same scopes RetentionTermToolkit.ps1 requests when connecting to Graph.
        internal static readonly string[] RequiredRetentionGraphScopes = { "Directory.ReadWrite.All", "Group.ReadWrite.All", "User.ReadWrite.All" };

        /// <summary>Mirrors the script's startup check of the fields the retention workflow cannot run without.</summary>
        public static void ValidateRetention(ToolConfig config)
        {
            var ad = config.Settings?.ActiveDirectory;
            var licenses = config.Settings?.LicenseGroups;
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(ad?.DomainNetbios)) missing.Add("Settings.ActiveDirectory.DomainNetbios");
            if (string.IsNullOrWhiteSpace(ad?.DomainDNS)) missing.Add("Settings.ActiveDirectory.DomainDNS");
            if (string.IsNullOrWhiteSpace(ad?.PrimaryDomainController)) missing.Add("Settings.ActiveDirectory.PrimaryDomainController");
            if (string.IsNullOrWhiteSpace(ad?.DisabledUsersOU)) missing.Add("Settings.ActiveDirectory.DisabledUsersOU");
            if (string.IsNullOrWhiteSpace(licenses?.M365G3?.GroupId)) missing.Add("Settings.LicenseGroups.M365_G3.GroupId");
            if (string.IsNullOrWhiteSpace(licenses?.M365G5?.GroupId)) missing.Add("Settings.LicenseGroups.M365_G5.GroupId");
            if (string.IsNullOrWhiteSpace(licenses?.PrimaryLicenseGroup)) missing.Add("Settings.LicenseGroups.PrimaryLicenseGroup");
            else if (string.IsNullOrWhiteSpace(licenses.GetPrimaryRetentionGroupId())) missing.Add("Settings.LicenseGroups.PrimaryLicenseGroup (must be M365_G3 or M365_G5 with a GroupId)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException("Configuration validation failed. Missing or invalid required fields:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
            }
        }
        /// <summary>
        /// Returns a writable directory for config.json. MSIX-packaged installs run from a read-only
        /// location under Program Files\WindowsApps, so runtime edits (Settings page Save) must be
        /// persisted under the per-user LocalAppData folder instead of the app's install directory.
        /// </summary>
        private static string GetWritableConfigDirectory()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermKit");
            Directory.CreateDirectory(directory);
            return directory;
        }

        // Lets an update replace a config seeded from the sample; the previous file is kept as config.json.bak.
        internal static ToolConfig? TryAdoptBundledConfig(ToolConfig local, string configPath, string bundledPath)
        {
            if (local.Settings?.ActiveDirectory is not { } localAd || !AdControllerDiscovery.IsSamplePlaceholder(localAd)) return null;
            try
            {
                if (!File.Exists(bundledPath)) return null;
                var bundledJson = File.ReadAllText(bundledPath);
                var bundled = JsonSerializer.Deserialize<ToolConfig>(bundledJson);
                if (bundled?.Settings?.ActiveDirectory is not { } bundledAd || AdControllerDiscovery.IsSamplePlaceholder(bundledAd)) return null;
                File.Copy(configPath, configPath + ".bak", true);
                File.WriteAllText(configPath, bundledJson);
                Logger.Log("Replaced the sample placeholder config.json with the configuration shipped in this version. The previous file is config.json.bak.", LogType.Warning);
                return bundled;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        public static ToolConfig Load(string? configDirectory = null)
        {
            var directory = configDirectory ?? GetWritableConfigDirectory();
            var configPath = Path.Combine(directory, "config.json");

            if (!File.Exists(configPath))
            {
                // First run (or no prior save): seed the writable location from the bundled config.json
                // shipped alongside the app, if one exists.
                var bundledPath = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (configDirectory == null && File.Exists(bundledPath))
                {
                    File.Copy(bundledPath, configPath);
                }
            }

            if (!File.Exists(configPath))
            {
                throw new InvalidOperationException($"Configuration file 'config.json' not found in the application directory: {directory}");
            }

            ToolConfig? config;
            try
            {
                var json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<ToolConfig>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse 'config.json': {ex.Message}", ex);
            }

            if (config == null)
            {
                throw new InvalidOperationException("Failed to load 'config.json': file is empty or invalid.");
            }

            if (configDirectory == null)
            {
                config = TryAdoptBundledConfig(config, configPath, Path.Combine(AppContext.BaseDirectory, "config.json")) ?? config;
            }
            // Backfill retention fields from the bundled config when users already have an older
            // local config.json created before the TermKit retention workflow existed.
            try
            {
                var bundledPath = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (File.Exists(bundledPath))
                {
                    var bundledJson = File.ReadAllText(bundledPath);
                    var bundledConfig = JsonSerializer.Deserialize<ToolConfig>(bundledJson);
                    if (bundledConfig?.Settings?.ActiveDirectory != null && config.Settings.ActiveDirectory == null)
                    {
                        config.Settings.ActiveDirectory = bundledConfig.Settings.ActiveDirectory;
                    }

                    if (string.IsNullOrWhiteSpace(config.Settings.UpdateFeedUrl) && !string.IsNullOrWhiteSpace(bundledConfig?.Settings?.UpdateFeedUrl))
                    {
                        config.Settings.UpdateFeedUrl = bundledConfig!.Settings.UpdateFeedUrl;
                    }

                    if (config.Settings.LicenseGroups.M365G3 == null && bundledConfig?.Settings?.LicenseGroups?.M365G3 != null)
                    {
                        config.Settings.LicenseGroups.M365G3 = bundledConfig.Settings.LicenseGroups.M365G3;
                    }

                    if (config.Settings.LicenseGroups.M365G5 == null && bundledConfig?.Settings?.LicenseGroups?.M365G5 != null)
                    {
                        config.Settings.LicenseGroups.M365G5 = bundledConfig.Settings.LicenseGroups.M365G5;
                    }

                    if (string.IsNullOrWhiteSpace(config.Settings.LicenseGroups.PrimaryLicenseGroup) &&
                        !string.IsNullOrWhiteSpace(bundledConfig?.Settings?.LicenseGroups?.PrimaryLicenseGroup))
                    {
                        config.Settings.LicenseGroups.PrimaryLicenseGroup = bundledConfig!.Settings.LicenseGroups.PrimaryLicenseGroup;
                    }
                }
            }
            catch
            {
                // Non-fatal: keep best-effort local config if backfill fails.
            }

            Validate(config);
            foreach (var scope in RequiredRetentionGraphScopes)
            {
                if (!config.Settings.GraphApi.Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase)) config.Settings.GraphApi.Scopes.Add(scope);
            }
            return config;
        }

        /// <summary>
        /// Validates and writes the given config back to config.json, so the in-app Settings
        /// section can persist edits made at runtime.
        /// </summary>
        public static void Save(ToolConfig config, string? configDirectory = null)
        {
            Validate(config);

            var directory = configDirectory ?? GetWritableConfigDirectory();
            var configPath = Path.Combine(directory, "config.json");

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }

        private static void Validate(ToolConfig config)
        {
            var missingFields = new List<string>();

            if (config.Settings?.GraphApi?.Scopes == null || config.Settings.GraphApi.Scopes.Count == 0)
            {
                missingFields.Add("Settings.GraphApi.Scopes");
            }

            if (missingFields.Count > 0)
            {
                throw new InvalidOperationException("Configuration validation failed. Missing or null required fields:" + Environment.NewLine + string.Join(Environment.NewLine, missingFields));
            }

            if (config.Settings!.GraphApi.Scopes.Exists(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("Settings.GraphApi.Scopes contains one or more empty values.");
            }
        }
    }
}
