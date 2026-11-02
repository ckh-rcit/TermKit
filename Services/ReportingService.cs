using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TermKit.Services
{
    /// <summary>
    /// A single row of a generated report (membership/ownership role or a mailbox permission entry).
    /// </summary>
    public class ReportRow
    {
        public string ObjectName { get; set; } = string.Empty;
        public string ObjectPrimarySmtpAddress { get; set; } = string.Empty;
        public string ObjectType { get; set; } = string.Empty;
        public string MemberOrDelegate { get; set; } = string.Empty;
        public string RoleOrPermission { get; set; } = string.Empty;
    }

    /// <summary>
    /// Ports the reporting logic from M365_Reporting_Tool.ps1 (per-object membership/permission report,
    /// exportable to CSV) and DelegateReporter.ps1 (quick mailbox delegate lookup), combined into a
    /// single Reporting section backed by the existing EXO PowerShell connection.
    /// </summary>
    public class ReportingService
    {
        private readonly ExoPowerShellService _exo;

        public ReportingService(ExoPowerShellService exo)
        {
            _exo = exo;
        }

        /// <summary>
        /// Generates the full membership/ownership or permission report for a single Group/DL/DDG/
        /// Security Group/Mailbox, mirroring M365_Reporting_Tool.ps1's Get-Recipient dispatch logic.
        /// </summary>
        public async Task<List<ReportRow>> GenerateObjectReportAsync(string identity)
        {
            var recipient = await _exo.GetRecipientAsync(identity);
            if (recipient == null)
            {
                throw new InvalidOperationException($"Could not find a recipient matching '{identity}'.");
            }

            var objectName = recipient.Name ?? identity;
            var objectSmtp = recipient.PrimarySmtpAddress ?? identity;
            var recipientType = recipient.RecipientTypeDetails ?? string.Empty;
            var rows = new List<ReportRow>();

            switch (recipientType)
            {
                case "GroupMailbox":
                    {
                        var links = await _exo.GetUnifiedGroupLinksAsync(recipient.Identity ?? identity);
                        rows.AddRange(links.Select(l => new ReportRow
                        {
                            ObjectName = objectName,
                            ObjectPrimarySmtpAddress = objectSmtp,
                            ObjectType = "Microsoft 365 Group",
                            MemberOrDelegate = $"{l.DisplayName} ({l.PrimarySmtpAddress})",
                            RoleOrPermission = l.Role
                        }));
                        break;
                    }
                case "MailUniversalDistributionGroup":
                    {
                        var links = await _exo.GetDistributionGroupReportLinksAsync(recipient.Identity ?? identity, isDynamic: false);
                        rows.AddRange(links.Select(l => new ReportRow
                        {
                            ObjectName = objectName,
                            ObjectPrimarySmtpAddress = objectSmtp,
                            ObjectType = "Distribution List",
                            MemberOrDelegate = $"{l.DisplayName} ({l.PrimarySmtpAddress})",
                            RoleOrPermission = l.Role
                        }));
                        break;
                    }
                case "DynamicDistributionGroup":
                    {
                        var links = await _exo.GetDistributionGroupReportLinksAsync(recipient.Identity ?? identity, isDynamic: true);
                        rows.AddRange(links.Select(l => new ReportRow
                        {
                            ObjectName = objectName,
                            ObjectPrimarySmtpAddress = objectSmtp,
                            ObjectType = "Dynamic Distribution Group",
                            MemberOrDelegate = $"{l.DisplayName} ({l.PrimarySmtpAddress})",
                            RoleOrPermission = l.Role
                        }));
                        break;
                    }
                case "MailUniversalSecurityGroup":
                    {
                        var links = await _exo.GetDistributionGroupReportLinksAsync(recipient.Identity ?? identity, isDynamic: false);
                        rows.AddRange(links.Select(l => new ReportRow
                        {
                            ObjectName = objectName,
                            ObjectPrimarySmtpAddress = objectSmtp,
                            ObjectType = "Mail-Enabled Security Group",
                            MemberOrDelegate = $"{l.DisplayName} ({l.PrimarySmtpAddress})",
                            RoleOrPermission = l.Role
                        }));
                        break;
                    }
                case "SharedMailbox":
                case "RoomMailbox":
                case "EquipmentMailbox":
                case "UserMailbox":
                    {
                        rows.AddRange(await BuildMailboxPermissionRowsAsync(recipient.Identity ?? identity, objectName, objectSmtp, recipientType));
                        break;
                    }
                default:
                    throw new InvalidOperationException($"The object type '{recipientType}' is not supported for reporting.");
            }

            return rows;
        }

        /// <summary>
        /// Quick single-mailbox delegate lookup (Full Access / Send As / Send on Behalf), ported from
        /// DelegateReporter.ps1's Get-MailboxDelegates.
        /// </summary>
        public async Task<List<ReportRow>> GetMailboxDelegatesAsync(string mailboxIdentity)
        {
            var exists = await _exo.MailboxExistsAsync(mailboxIdentity);
            if (!exists)
            {
                throw new InvalidOperationException($"Could not find or access mailbox '{mailboxIdentity}'.");
            }

            var recipient = await _exo.GetRecipientAsync(mailboxIdentity);
            var objectName = recipient?.Name ?? mailboxIdentity;
            var objectSmtp = recipient?.PrimarySmtpAddress ?? mailboxIdentity;
            var recipientType = recipient?.RecipientTypeDetails ?? string.Empty;

            return await BuildMailboxPermissionRowsAsync(mailboxIdentity, objectName, objectSmtp, recipientType);
        }

        private async Task<List<ReportRow>> BuildMailboxPermissionRowsAsync(string mailboxIdentity, string objectName, string objectSmtp, string recipientType)
        {
            var rows = new List<ReportRow>();

            var fullAccess = await _exo.GetAllFullAccessDelegatesAsync(mailboxIdentity);
            rows.AddRange(fullAccess.Select(u => new ReportRow
            {
                ObjectName = objectName,
                ObjectPrimarySmtpAddress = objectSmtp,
                ObjectType = recipientType,
                MemberOrDelegate = u,
                RoleOrPermission = "Full Access"
            }));

            var sendAs = await _exo.GetAllSendAsDelegatesAsync(mailboxIdentity);
            rows.AddRange(sendAs.Select(u => new ReportRow
            {
                ObjectName = objectName,
                ObjectPrimarySmtpAddress = objectSmtp,
                ObjectType = recipientType,
                MemberOrDelegate = u,
                RoleOrPermission = "Send As"
            }));

            var sendOnBehalf = await _exo.GetAllSendOnBehalfDelegatesAsync(mailboxIdentity);
            rows.AddRange(sendOnBehalf.Select(u => new ReportRow
            {
                ObjectName = objectName,
                ObjectPrimarySmtpAddress = objectSmtp,
                ObjectType = recipientType,
                MemberOrDelegate = u,
                RoleOrPermission = "Send on Behalf"
            }));

            return rows;
        }
    }
}
