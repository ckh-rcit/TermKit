# Copilot Instructions

## Project Guidelines
- TermKit project: EXO permission-related PowerShell cmdlets (Add/Remove-MailboxPermission, Add/Remove-RecipientPermission, and their Get- existence-check counterparts) can throw a Microsoft-side 'Write-ErrorMessage: Object reference not set' server bug on M365 Group-backed mailboxes; all such calls (including existence pre-checks, not just mutations) must be wrapped in retry-and-verify logic rather than treated as hard failures.