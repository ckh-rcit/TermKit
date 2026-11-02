using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace TermKit.Services
{
    /// <summary>
    /// Kiota authentication provider that fetches an MSAL access token (silent-then-interactive)
    /// for a fixed set of scopes and attaches it to outgoing Graph requests. Ported from Entra Scout.
    /// </summary>
    public class TokenAuthenticationProvider : IAuthenticationProvider
    {
        private readonly AuthService _authService;
        private readonly string[] _scopes;

        public TokenAuthenticationProvider(AuthService authService, string[] scopes)
        {
            _authService = authService;
            _scopes = scopes;
        }

        public async Task AuthenticateRequestAsync(
            RequestInformation request,
            System.Collections.Generic.Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            var token = await _authService.GetAccessTokenAsync(_scopes);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Add("Authorization", $"Bearer {token}");
            }
        }
    }
}
