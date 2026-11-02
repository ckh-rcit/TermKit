using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Language;
using Microsoft.Identity.Client;
using TermKit.Services;
using Xunit;

namespace TermKit.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public void KeyVaultRequiresConnectedTenantAndUsesBrowserRedirect()
    {
        Assert.Throws<InvalidOperationException>(() => ServiceNowService.CreateCredentialOptions(null));
        var options = ServiceNowService.CreateCredentialOptions("11111111-1111-1111-1111-111111111111");
        Assert.Equal("11111111-1111-1111-1111-111111111111", options.TenantId);
        Assert.Equal("http://localhost/", options.RedirectUri.AbsoluteUri);
    }
    [Fact]
    public async Task PermissionVerificationAcceptsAppliedWriteDespiteServerBug()
    {
        var granted = false;
        var attempts = 0;
        await PermissionVerification.ApplyAsync(() =>
        {
            attempts++;
            granted = true;
            throw new InvalidOperationException("Write-ErrorMessage: Object reference not set");
        }, () => Task.FromResult(granted), true, () => Task.CompletedTask);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FailedPermissionReadIsNeverTreatedAsAbsent()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PermissionVerification.ReadAsync<bool>(() =>
        {
            attempts++;
            throw new InvalidOperationException("Object reference not set");
        }, () => Task.CompletedTask));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task MailboxAccessFailureStopsPrecheck()
    {
        using var runspace = OnPremAdService.CreateRunspace();
        using var powershell = PowerShell.Create();
        powershell.Runspace = runspace;
        powershell.AddScript("""
            function Get-EXOMailbox { [CmdletBinding()] param($Identity)
                $PSCmdlet.WriteError([Management.Automation.ErrorRecord]::new([UnauthorizedAccessException]::new('Access denied'), 'AccessDenied', [Management.Automation.ErrorCategory]::PermissionDenied, $Identity))
            }
            """);
        powershell.Invoke();
        var exo = new ExoPowerShellService(runspace);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => exo.GetMailboxRecipientTypeDetailsAsync("mailbox", true)); }
        finally { await exo.ShutdownAsync(); }
    }

    [Fact]
    public async Task FullAccessPrecheckRetriesServerBugAndReadsArrayList()
    {
        using var runspace = OnPremAdService.CreateRunspace();
        using var powershell = PowerShell.Create();
        powershell.Runspace = runspace;
        powershell.AddScript("""
            function Get-MailboxPermission { [CmdletBinding()] param($Identity,$User)
                $global:attempts++
                if ($global:attempts -eq 1) { throw 'Object reference not set' }
                [pscustomobject]@{AccessRights=[Collections.ArrayList]@('FullAccess');Deny=$false;IsInherited=$false}
            }
            """);
        powershell.Invoke();
        var exo = new ExoPowerShellService(runspace);
        try
        {
            Assert.True(await exo.HasDirectFullAccessAsync("mailbox", "delegate"));
            Assert.Equal(2, runspace.SessionStateProxy.GetVariable("attempts"));
        }
        finally { await exo.ShutdownAsync(); }
    }

    [Fact]
    public async Task TypedMissingMailboxRemainsAbsent()
    {
        using var runspace = OnPremAdService.CreateRunspace();
        using var powershell = PowerShell.Create();
        powershell.Runspace = runspace;
        powershell.AddScript("""
            function Get-EXOMailbox { [CmdletBinding()] param($Identity)
                $PSCmdlet.WriteError([Management.Automation.ErrorRecord]::new([InvalidOperationException]::new('Mailbox absent'), 'ManagementObjectNotFoundException', [Management.Automation.ErrorCategory]::ObjectNotFound, $Identity))
            }
            """);
        powershell.Invoke();
        var exo = new ExoPowerShellService(runspace);
        try { Assert.Null(await exo.GetMailboxRecipientTypeDetailsAsync("mailbox", true)); }
        finally { await exo.ShutdownAsync(); }
    }

    [Fact]
    public void EmbeddedHostPreparesExoDependenciesWithoutSignIn()
    {
        using var runspace = OnPremAdService.CreateRunspace();
        using var powershell = PowerShell.Create();
        powershell.Runspace = runspace;
        powershell.AddScript(PowerShellPrerequisites.Script);
        powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join("; ", powershell.Streams.Error));
    }

    [Fact]
    public async Task GraphRequiresExchangeTenantBeforeSignIn()
    {
        var auth = new AuthService(new[] { "User.Read" }, () => IntPtr.Zero);
        var application = (IPublicClientApplication)typeof(AuthService).GetField("_app", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(auth)!;
        Assert.Equal("http://localhost", application.AppConfig.RedirectUri);
        Assert.False(await auth.ConnectGraphAsync());
        Assert.Contains("Exchange Online first", auth.LastError);
        Assert.False(auth.IsGraphConnected);
        Assert.Null(auth.ConnectedTenantId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetAccessTokenAsync());
    }

    [Theory]
    [InlineData("Connected", "Active", "11111111-1111-1111-1111-111111111111", true)]
    [InlineData("Connected", "Expired", "11111111-1111-1111-1111-111111111111", false)]
    [InlineData("Disconnected", "Active", "11111111-1111-1111-1111-111111111111", false)]
    [InlineData("Connected", "Active", "", false)]
    public void ExchangeRequiresActiveTokenAndTenant(string state, string token, string tenant, bool valid)
    {
        var connection = new PSObject();
        connection.Properties.Add(new PSNoteProperty("State", state));
        connection.Properties.Add(new PSNoteProperty("TokenStatus", token));
        connection.Properties.Add(new PSNoteProperty("TenantID", tenant));
        if (valid) ExoPowerShellService.ValidateConnection(connection);
        else Assert.Throws<InvalidOperationException>(() => ExoPowerShellService.ValidateConnection(connection));
    }

    [Fact]
    public void PublisherPromptCannotSilentlyTrustCode()
    {
        var host = new LoggerPSHost();
        Assert.Throws<InvalidOperationException>(() => host.UI.PromptForChoice("Publisher", "Trust?", new Collection<ChoiceDescription> { new("Yes"), new("No") }, 0));
    }

    [Fact]
    public void PrerequisiteScriptParsesWithoutDeviceOrAuthorizationBypass()
    {
        Parser.ParseInput(PowerShellPrerequisites.Script, out _, out var errors);
        Assert.Empty(errors);
        Assert.DoesNotContain("UseDevice", PowerShellPrerequisites.Script);
        Assert.DoesNotContain("Bypass", PowerShellPrerequisites.Script);
        Assert.Contains("PackageManagement", PowerShellPrerequisites.Script);
        Assert.Contains("PowerShellGet", PowerShellPrerequisites.Script);
    }

    [RequiresAdModuleFact]
    public void EmbeddedHostImportsInstalledAdWithoutCredentials()
    {
        using var runspace = OnPremAdService.CreateRunspace();
        using var powershell = PowerShell.Create();
        powershell.Runspace = runspace;
        powershell.AddScript("Import-Module ActiveDirectory -ErrorAction Stop; Get-Command Get-ADDomain,Get-ADUser,Set-ADUser,Move-ADObject -ErrorAction Stop");
        var commands = powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join("; ", powershell.Streams.Error));
        Assert.Equal(4, commands.Count);
    }
}
