using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace TermKit.Services
{
    /// <summary>
    /// Wraps MSAL.NET interactive public-client authentication for Microsoft Graph, ported from
    /// Entra Scout's AuthService. The toolkit connects to Exchange Online first (see
    /// ExoPowerShellService), then Microsoft Graph using the scopes configured in config.json
    /// (Settings.GraphApi.Scopes), matching the script's enforced EXO-then-Graph connection order.
    /// </summary>
    public class AuthService
    {
        // Microsoft Graph PowerShell well-known public client ID (multi-tenant, pre-registered redirect URIs).
        private const string ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
        private const string Authority = "https://login.microsoftonline.com/organizations";

        private readonly string[] _graphScopes;
        private readonly IPublicClientApplication _app;
        private readonly Func<IntPtr> _parentWindowHandleProvider;

        public bool IsGraphConnected { get; private set; }
        public string? ConnectedUser { get; private set; }
        public string? ConnectedTenantId { get; private set; }
        public string? LastError { get; private set; }
        private string? _tenantId;

        public AuthService(string[] graphScopes, Func<IntPtr> parentWindowHandleProvider)
        {
            _graphScopes = graphScopes;
            _parentWindowHandleProvider = parentWindowHandleProvider;
            _app = PublicClientApplicationBuilder.Create(ClientId)
                .WithAuthority(Authority)
                .WithRedirectUri("http://localhost")
                .Build();
        }

        public async Task<bool> ConnectGraphAsync(string? expectedTenantId = null)
        {
            try
            {
                LastError = null;
                if (!Guid.TryParse(expectedTenantId, out _)) throw new InvalidOperationException("Connect to Exchange Online first to establish the tenant.");
                _tenantId = expectedTenantId;
                Logger.Log("Connecting to Microsoft Graph...");
                var result = await AcquireTokenAsync(_graphScopes);
                if (result == null || !string.Equals(result.TenantId, _tenantId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Graph and Exchange Online must use the same tenant.");
                IsGraphConnected = result != null;
                if (result != null)
                {
                    ConnectedUser = result.Account?.Username;
                    ConnectedTenantId = result.TenantId;
                    Logger.Log($"Connected to Microsoft Graph as {ConnectedUser}.", LogType.Success);
                }
                return IsGraphConnected;
            }
            catch (Exception ex)
            {
                IsGraphConnected = false;
                ConnectedUser = null;
                ConnectedTenantId = null;
                LastError = ex.Message;
                Logger.Log($"Graph connection failed: {ex.Message}", LogType.Error);
                return false;
            }
        }

        public async Task DisconnectGraphAsync()
        {
            try
            {
                var accounts = await _app.GetAccountsAsync();
                foreach (var account in accounts)
                {
                    await _app.RemoveAsync(account);
                }
                IsGraphConnected = false;
                ConnectedUser = null;
                Logger.Log("Disconnected from Microsoft Graph.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Graph disconnect error: {ex.Message}", LogType.Error);
            }
            finally
            {
                IsGraphConnected = false;
                ConnectedUser = null;
                ConnectedTenantId = null;
                _tenantId = null;
            }
        }

        public async Task<string?> GetAccessTokenAsync(string[]? scopes = null)
        {
            if (!IsGraphConnected) throw new InvalidOperationException("Connect to Microsoft Graph first.");
            var result = await AcquireTokenAsync(scopes ?? _graphScopes);
            if (!string.Equals(result?.TenantId, ConnectedTenantId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Graph token tenant changed. Disconnect and reconnect.");
            return result?.AccessToken;
        }

        private async Task<AuthenticationResult?> AcquireTokenAsync(string[] scopes)
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            string? claims = null;
            var accounts = await _app.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            if (account != null)
            {
                try
                {
                    return await _app.AcquireTokenSilent(scopes, account).WithTenantId(_tenantId).ExecuteAsync(timeout.Token);
                }
                catch (MsalUiRequiredException exception)
                {
                    claims = exception.Claims;
                }
            }

            return await _app.AcquireTokenInteractive(scopes)
                .WithTenantId(_tenantId)
                .WithClaims(claims)
                .WithUseEmbeddedWebView(false)
                .WithParentActivityOrWindow(_parentWindowHandleProvider())
                .ExecuteAsync(timeout.Token);
        }
    }
}
