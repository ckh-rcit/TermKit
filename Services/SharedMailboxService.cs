using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public class SharedMailboxCreationRequest
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Department { get; set; }
        public bool EnableArchive { get; set; }
        public bool HideFromAddressLists { get; set; }
        public bool RequireSenderAuthentication { get; set; }
        public string[] AcceptedSenders { get; set; } = Array.Empty<string>();
        public string[] BlockedSenders { get; set; } = Array.Empty<string>();
        public string[] FullAccessUsers { get; set; } = Array.Empty<string>();
        public string[] SendAsUsers { get; set; } = Array.Empty<string>();
        public string[] SendOnBehalfUsers { get; set; } = Array.Empty<string>();
    }

    public class SharedMailboxCreationResult
    {
        public bool Success { get; set; }
        public List<string> Actions { get; } = new();
        public string TicketSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Ports the "Create New Shared Mailbox" workflow from EXO-SharedMailbox.ps1's $BtnCreate.Add_Click
    /// handler: creates the mailbox, applies department/archive/GAL settings, applies delivery
    /// restrictions, then assigns initial Full Access / Send As / Send on Behalf delegation using the
    /// same permission wrappers already used by the Mailboxes section.
    /// </summary>
    public class SharedMailboxService
    {
        private readonly ExoPowerShellService _exo;

        public SharedMailboxService(ExoPowerShellService exo)
        {
            _exo = exo;
        }

        public async Task<SharedMailboxCreationResult> CreateSharedMailboxAsync(SharedMailboxCreationRequest request)
        {
            var result = new SharedMailboxCreationResult();

            Logger.Log("--- Starting Shared Mailbox Creation ---");
            Logger.Log($"Executing: New-Mailbox -Name '{request.DisplayName}' -DisplayName '{request.DisplayName}' -PrimarySmtpAddress '{request.Email}' -Shared");

            try
            {
                await _exo.CreateSharedMailboxAsync(request.DisplayName, request.Email);
                result.Actions.Add("Mailbox Created");
                Logger.Log($"  SUCCESS: Mailbox '{request.Email}' created.");
                await WaitForMailboxVisibleAsync(request.Email);
            }
            catch (Exception ex)
            {
                Logger.Log($"  ERROR: Failed to create mailbox '{request.Email}'. DETAILS: {ex.Message}", LogType.Error);
                result.Actions.Add("ERROR Creating Mailbox");
                result.Success = false;
                return result;
            }

            if (!string.IsNullOrWhiteSpace(request.Department))
            {
                try
                {
                    await _exo.SetUserDepartmentAsync(request.Email, request.Department!);
                    result.Actions.Add("Department Set");
                    Logger.Log($"  Department set to '{request.Department}'.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"  ERROR: Failed to set Department. DETAILS: {ex.Message}", LogType.Error);
                    result.Actions.Add("ERROR Setting Department");
                }
            }

            if (request.EnableArchive)
            {
                try
                {
                    await _exo.EnableMailboxArchiveAsync(request.Email);
                    result.Actions.Add("Archive Enabled");
                    Logger.Log("  Archive mailbox enabled.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"  ERROR: Failed to enable Archive. DETAILS: {ex.Message}", LogType.Error);
                    result.Actions.Add("ERROR Enabling Archive");
                }
            }

            if (request.HideFromAddressLists)
            {
                try
                {
                    await _exo.SetHiddenFromAddressListsAsync(request.Email, true);
                    result.Actions.Add("Hidden From GAL");
                    Logger.Log("  Mailbox hidden from address lists.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"  ERROR: Failed to hide from GAL. DETAILS: {ex.Message}", LogType.Error);
                    result.Actions.Add("ERROR Hiding From GAL");
                }
            }

            if (request.RequireSenderAuthentication || request.AcceptedSenders.Length > 0 || request.BlockedSenders.Length > 0)
            {
                try
                {
                    await _exo.SetDeliveryRestrictionsAsync(request.Email, request.RequireSenderAuthentication, request.AcceptedSenders, request.BlockedSenders);
                    result.Actions.Add("Delivery Restrictions Applied");
                    Logger.Log("  Delivery restrictions applied.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"  ERROR: Failed to apply delivery restrictions. DETAILS: {ex.Message}", LogType.Error);
                    result.Actions.Add("ERROR Applying Delivery Restrictions");
                }
            }

            await Wait("Full Access", request.FullAccessUsers, u => _exo.AddFullAccessAsync(request.Email, u), result);
            await Wait("Send As", request.SendAsUsers, u => _exo.AddSendAsAsync(request.Email, u), result);

            if (request.SendOnBehalfUsers.Length > 0)
            {
                try
                {
                    await _exo.AddSendOnBehalfBatchAsync(request.Email, request.SendOnBehalfUsers);
                    result.Actions.Add($"Send on Behalf granted to {request.SendOnBehalfUsers.Length} user(s)");
                    foreach (var u in request.SendOnBehalfUsers)
                    {
                        Logger.Log($"  Send on Behalf granted to '{u}'.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"  ERROR: Failed to grant Send on Behalf. DETAILS: {ex.Message}", LogType.Error);
                    result.Actions.Add("ERROR Granting Send on Behalf");
                }
            }

            result.Success = true;
            result.TicketSummary = BuildTicketSummary(request, result);

            Logger.Log("--- For IT Ticket ---", LogType.Ticket);
            foreach (var line in result.TicketSummary.Split(Environment.NewLine))
            {
                Logger.Log(line, LogType.Ticket);
            }
            Logger.Log("---------------------", LogType.Ticket);

            Logger.Log("--- Shared Mailbox Creation Complete ---");

            return result;
        }

        private const int ReplicationAttempts = 8;
        private static readonly TimeSpan ReplicationDelay = TimeSpan.FromSeconds(5);

        // New-Mailbox can return before the mailbox is visible to every Exchange backend, so a follow-up cmdlet may report it missing.
        private static bool IsMailboxNotYetVisible(Exception ex) =>
            ex.Message.Contains("ManagementObjectNotFoundException", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("as a recipient", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase);

        private async Task WaitForMailboxVisibleAsync(string email)
        {
            for (var attempt = 1; attempt <= ReplicationAttempts; attempt++)
            {
                try
                {
                    if (await _exo.MailboxExistsAsync(email)) return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Log($"  Mailbox visibility check failed: {ex.Message}", LogType.Warning);
                }
                Logger.Log($"  Waiting for '{email}' to replicate ({attempt}/{ReplicationAttempts})...", LogType.Warning);
                await Task.Delay(ReplicationDelay);
            }
            Logger.Log($"  Mailbox '{email}' is not yet visible; permission steps will retry.", LogType.Warning);
        }

        private static async Task Wait(string label, string[] users, Func<string, Task> action, SharedMailboxCreationResult result)
        {
            foreach (var user in users)
            {
                var objectRefRetried = false;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await action(user);
                        result.Actions.Add($"{label} granted to {user}");
                        Logger.Log($"  {label} granted to '{user}'.");
                        break;
                    }
                    catch (Exception ex) when (!objectRefRetried && ex.Message.Contains("Object reference not set to an instance of an object", StringComparison.OrdinalIgnoreCase))
                    {
                        objectRefRetried = true;
                        Logger.Log($"  {label} for '{user}' returned server error (Object reference not set). Retrying once...", LogType.Warning);
                        await Task.Delay(2000);
                    }
                    catch (Exception ex) when (attempt < ReplicationAttempts && IsMailboxNotYetVisible(ex))
                    {
                        Logger.Log($"  {label} for '{user}': mailbox not yet replicated. Retrying ({attempt}/{ReplicationAttempts})...", LogType.Warning);
                        await Task.Delay(ReplicationDelay);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"  ERROR: Failed to grant {label} to '{user}'. DETAILS: {ex.Message}", LogType.Error);
                        result.Actions.Add($"ERROR Granting {label} to {user}");
                        break;
                    }
                }
            }
        }

        private static string BuildTicketSummary(SharedMailboxCreationRequest request, SharedMailboxCreationResult result)
        {
            var lines = new List<string>
            {
                "Shared Mailbox Created:",
                $"  Display Name: {request.DisplayName}",
                $"  Email: {request.Email}"
            };

            if (!string.IsNullOrWhiteSpace(request.Department)) lines.Add($"  Department: {request.Department}");
            if (request.EnableArchive) lines.Add("  Archive: Enabled");
            if (request.HideFromAddressLists) lines.Add("  Hidden From GAL: Yes");
            if (request.FullAccessUsers.Length > 0) lines.Add($"  Full Access: {string.Join(", ", request.FullAccessUsers)}");
            if (request.SendAsUsers.Length > 0) lines.Add($"  Send As: {string.Join(", ", request.SendAsUsers)}");
            if (request.SendOnBehalfUsers.Length > 0) lines.Add($"  Send on Behalf: {string.Join(", ", request.SendOnBehalfUsers)}");

            return string.Join(Environment.NewLine, lines);
        }
    }
}
