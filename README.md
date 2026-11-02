# TermKit

TermKit is the WinUI 3 (.NET 10) rewrite of the PowerShell-based **Retention Term Toolkit**. It hosts the AD, Exchange Online, and Microsoft Graph operations used by the shared-mailbox retention workflow.

## Authentication

- EXO uses interactive browser sign-in with `DisableWAM` for the embedded GUI host. No device-code sign-in or fallback is used.
- Connect EXO before Graph. Graph uses a localhost browser redirect and the EXO tenant. A read-only Graph/EXO check must pass before conversion is enabled. Consent, MFA, and Conditional Access still apply.
- AD uses the credentials entered in the application and tries the configured domain controllers. Connection failures retain the controller error. RSAT Active Directory tools must be installed by an administrator; TermKit does not elevate itself or install Windows capabilities.
- Optional **Remember on this PC** in the AD dialog stores the username and password in Windows Credential Manager (`TermKit:AD:<DOMAIN>`), encrypted by Windows for your account and kept on this PC only. Nothing is written to `config.json` or the logs. Connect Active Directory then signs in with the saved credentials, one attempt per click and never automatically at startup. If the domain rejects them (for example after a password change), TermKit stops trying other controllers and asks for the new password; unchecking Remember, or **Forget saved AD credentials** in the connections menu, deletes them. Other programs running as your Windows user could read the entry, as with any Credential Manager item.
- Optional Key Vault access uses a reusable browser credential scoped to the connected Graph tenant, with a five-minute request timeout. Graph, EXO, and Key Vault do not share a guaranteed token cache and may prompt separately.

## Prerequisites

The app embeds PowerShell 7.6.6. Before EXO sign-in it checks stable PSGallery versions and installs missing or newer PackageManagement, PowerShellGet, and ExchangeOnlineManagement modules under CurrentUser. Install PowerShell 7.6+ if PSResourceGet 1.2+ is unavailable. Suitable installed versions can be used when the update check is offline. Older loaded versions require restarting the app.

The hosted runspaces request process-scoped RemoteSigned and retain normal authorization. Group Policy still applies. Publisher prompts are never silently accepted: review the requested publisher in an interactive PowerShell session before retrying.

## Failures and Recovery

Connection and conversion failures display the underlying error. Managed startup/UI/background failures have a last-resort diagnostic dialog. Daily logs are written under `%LOCALAPPDATA%\TermKit\Logs`; review them before retrying. Applied changes are not automatically rolled back. Fatal failures do not leave the application running in an unknown state.

Connection controls and window closing are blocked while sign-in or conversion is running. EXO permission reads and writes use bounded retry-and-verify for the known server null-reference bug. Failed mailbox reads remain errors, and an unreadable mailbox size stops conversion instead of being treated as zero.

## Task Lookup

**Look up** next to Task # reads the ServiceNow task with the service account from Key Vault (read-only) and fills Term User, Delegate, and Retention End Date from the request's catalog variables. The delegate's ServiceNow user is resolved to an email address. Missing values, an unrecognized date, or a request that says email should not be retained produce a warning instead of a guess. Review the fields before processing. Connect Exchange Online (or Graph) first so the Key Vault sign-in uses the right tenant. The variable names are configurable under `Settings.ServiceNow` (`RetentionTermUserVariable`, `RetentionDelegateVariable`, `RetentionEndDateVariable`, `RetentionFlagVariable`).

## Terminations Without Email Retention

Tick **No email retention needed** (Look up ticks it when the request answers "No" to retaining email). The end date becomes tomorrow, which you can override, and the delegate becomes optional; with no delegate, no Full Access is granted. Everything else runs as usual, and the work notes say "no email retention requested".

The AD description keeps the exact `Shared Mailbox - DO NOT DELETE <Task #> - UNTIL <MM/DD/YYYY>` format. The scheduled `TerminateAccounts.ps1` job in the Actions repo finds accounts by that prefix and parses the date after `UNTIL `, so changing the wording would exclude the account from cleanup. That job acts on accounts whose date is before today.

## Local Verification

```powershell
dotnet test tests/TermKit.Tests/TermKit.Tests.csproj --configuration Release --runtime win-x64
dotnet build TermKit.csproj -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false
```

Tests exercise browser/tenant configuration, publisher rejection, hosted AD imports (skipped when RSAT is not installed), hosted EXO prerequisite preparation, permission/read-failure handling, the retention rules ported from the script (deleted-user matching, password complexity, configuration preflight, Graph scopes), and the ServiceNow close request. The bootstrap test contacts PSGallery and may install or update CurrentUser modules. No test signs in or changes tenant/directory data.

Live AD credentials, cloud sign-in, Key Vault access, and a controlled retention conversion still require operator validation. These checks do not establish complete workflow parity.

## Configuration

The packaged app seeds `%LOCALAPPDATA%\TermKit\config.json` from `config.sample.json` on first run. Replace the placeholder AD, license group, and ServiceNow values there. Retention conversion refuses to start while the AD domain, disabled-users OU, M365 G3/G5 license group IDs, or primary license group are missing. Graph always requests `Directory.ReadWrite.All`, `Group.ReadWrite.All`, and `User.ReadWrite.All`, the same scopes as the PowerShell toolkit.

## Build and Release

- `Validate` runs the test suite on every push and pull request to `main`.
- Pushing a `vMajor.Minor.Build` tag runs `Build and Release`: it stamps the package version from the tag, generates `TermKit.appinstaller`, runs the tests, builds a signed x64 MSIX, verifies the bundled PowerShell core modules, and publishes the MSIX, the appinstaller, and a certificate trust bundle.
- `Publish Update Feed` then deploys the download page and the update feed to GitHub Pages (`https://ckh-rcit.github.io/TermKit/`).
- The repository needs the `PACKAGE_CERTIFICATE_BASE64` and `PACKAGE_CERTIFICATE_PASSWORD` secrets and the `APPINSTALLER_FEED_URL` variable. The certificate subject must match the manifest publisher (`CN=CKH-RCIT`, the same certificate EXOKit uses, so machines that already trust EXOKit trust TermKit).
- Bump `Version` in `TermKit.csproj` and `Package.appxmanifest` before tagging; the tag must not be lower than the manifest version.

## Updates

Install TermKit from the download page by opening `TermKit.appinstaller` with Windows App Installer. Enrolled installs check the feed on launch and in the background. Installing the MSIX directly does not enroll the app.

TermKit also checks the feed itself at startup (`Settings.UpdateFeedUrl`, stamped into the packaged config) and shows **Update to vX.Y.Z** in the top bar when a newer version is published. The button opens the feed in Windows App Installer, which validates the package signature. The check only reads the feed over HTTPS, requires the feed's package name to match, and never installs anything itself.

References: [EXO prerequisites](https://learn.microsoft.com/powershell/exchange/exchange-online-powershell-v2), [WAM compatibility](https://learn.microsoft.com/troubleshoot/exchange/administration/wam-integration-issues), [MSAL browser configuration](https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/using-web-browsers), [Azure credential reuse](https://learn.microsoft.com/dotnet/azure/sdk/authentication/best-practices#reuse-credential-instances).
