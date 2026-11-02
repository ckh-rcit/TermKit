using System;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public class RecipientCheckResult
    {
        public bool Success { get; set; }
        public string DisplayType { get; set; } = string.Empty;
        public string LogMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Ports Get-RecipientType (Recipient Checker tab): resolves a recipient identity via Get-Recipient
    /// and maps its RecipientTypeDetails to the same friendly display strings used by the script.
    /// </summary>
    public class RecipientLookupService
    {
        private readonly ExoPowerShellService _exo;

        public RecipientLookupService(ExoPowerShellService exo)
        {
            _exo = exo;
        }

        public async Task<RecipientCheckResult> CheckRecipientTypeAsync(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity))
            {
                return new RecipientCheckResult
                {
                    Success = false,
                    DisplayType = "Enter email.",
                    LogMessage = "Recipient Check: No ID."
                };
            }

            Logger.Log($"Checking recipient type for '{identity}'...");

            try
            {
                var recipient = await _exo.GetRecipientAsync(identity);
                if (recipient == null)
                {
                    Logger.Log($"Recipient Check: '{identity}' not found (null).", LogType.Error);
                    return new RecipientCheckResult
                    {
                        Success = false,
                        DisplayType = "Not Found (Unexpected)",
                        LogMessage = $"Recipient Check: '{identity}' not found (null)."
                    };
                }

                var recipientDetails = recipient.RecipientTypeDetails ?? string.Empty;
                var displayType = recipientDetails switch
                {
                    "UserMailbox" => "User Mailbox",
                    "SharedMailbox" => "Shared Mailbox",
                    "RoomMailbox" => "Room Mailbox",
                    "EquipmentMailbox" => "Equipment Mailbox",
                    "MailUniversalDistributionGroup" => "Distribution Group",
                    "GroupMailbox" => "Microsoft 365 Group",
                    _ => $"{recipient.RecipientType} ({recipientDetails})"
                };

                Logger.Log($"Recipient Check: '{identity}' is '{displayType}'.", LogType.Success);
                return new RecipientCheckResult
                {
                    Success = true,
                    DisplayType = displayType,
                    LogMessage = $"Recipient Check: '{identity}' is '{displayType}'."
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"Recipient Check: '{identity}' error. DETAILS: {ex.Message}", LogType.Error);
                return new RecipientCheckResult
                {
                    Success = false,
                    DisplayType = "Not Found / Error",
                    LogMessage = $"Recipient Check: '{identity}' error. DETAILS: {ex.Message}"
                };
            }
        }
    }
}
