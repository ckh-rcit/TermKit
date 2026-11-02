using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace TermKit.Services
{
    public sealed class GraphUserRecord
    {
        public string Id { get; set; } = string.Empty;
        public string? UserPrincipalName { get; set; }
        public string? OnPremisesSamAccountName { get; set; }
        public string? Mail { get; set; }
    }

    /// <summary>
    /// Microsoft Graph operations for M365 Group membership/ownership and Graph user resolution,
    /// ported from the WinForms script's Get-MgUser / Get-MgGroupMember / New-MgGroupMember /
    /// New-MgGroupOwnerByRef / Remove-MgGroupMemberByRef / Remove-MgGroupOwnerByRef calls, plus the
    /// Bookings license group membership check/add used by Enable-BookingsAccessGUI.
    /// </summary>
    public class GraphService
    {
        private readonly AuthService _authService;
        private readonly string[] _scopes;
        private GraphServiceClient? _graphClient;
        private static readonly HttpClient _httpClient = new();

        public GraphService(AuthService authService, string[] scopes)
        {
            _authService = authService;
            _scopes = scopes;
        }

        public void InitializeGraphClient()
        {
            var authProvider = new TokenAuthenticationProvider(_authService, _scopes);
            _graphClient = new GraphServiceClient(authProvider);
        }

        private GraphServiceClient Client => _graphClient ?? throw new InvalidOperationException("Graph client not initialized. Connect to Microsoft Graph first.");

        public async Task VerifyConnectionAsync()
        {
            var user = await Client.Me.GetAsync(config => config.QueryParameters.Select = new[] { "id" });
            if (string.IsNullOrWhiteSpace(user?.Id)) throw new InvalidOperationException("Graph did not return the signed-in user.");
        }

        public async Task<string?> GetUserIdAsync(string userIdentity)
        {
            try
            {
                var user = await Client.Users[userIdentity].GetAsync(config =>
                {
                    config.QueryParameters.Select = new[] { "id" };
                });
                return user?.Id;
            }
            catch (ODataError odataEx)
            {
                Logger.Log($"Graph user lookup failed for '{userIdentity}': {odataEx.Error?.Code} - {odataEx.Error?.Message}", LogType.Error);
                return null;
            }
        }

        public async Task<bool> IsGroupMemberAsync(string groupId, string userId)
        {
            try
            {
                var response = await Client.Groups[groupId].Members.GraphUser.GetAsync(config =>
                {
                    config.QueryParameters.Filter = $"id eq '{userId}'";
                    config.QueryParameters.Count = true;
                    config.Headers.Add("ConsistencyLevel", "eventual");
                });
                return (response?.OdataCount ?? 0) > 0 || (response?.Value?.Count ?? 0) > 0;
            }
            catch (ODataError odataEx)
            {
                Logger.Log($"Graph group member check failed: {odataEx.Error?.Code} - {odataEx.Error?.Message}", LogType.Error);
                return false;
            }
        }

        public async Task<bool> IsGroupOwnerAsync(string groupId, string userId)
        {
            try
            {
                var response = await Client.Groups[groupId].Owners.GraphUser.GetAsync(config =>
                {
                    config.QueryParameters.Filter = $"id eq '{userId}'";
                    config.QueryParameters.Count = true;
                    config.Headers.Add("ConsistencyLevel", "eventual");
                });
                return (response?.OdataCount ?? 0) > 0 || (response?.Value?.Count ?? 0) > 0;
            }
            catch (ODataError odataEx)
            {
                Logger.Log($"Graph group owner check failed: {odataEx.Error?.Code} - {odataEx.Error?.Message}", LogType.Error);
                return false;
            }
        }

        public async Task AddGroupMemberAsync(string groupId, string userId)
        {
            var requestBody = new ReferenceCreate
            {
                OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{userId}"
            };
            await Client.Groups[groupId].Members.Ref.PostAsync(requestBody);
        }

        public async Task RemoveGroupMemberAsync(string groupId, string userId)
        {
            await Client.Groups[groupId].Members[userId].Ref.DeleteAsync();
        }

        public async Task AddGroupOwnerAsync(string groupId, string userId)
        {
            var requestBody = new ReferenceCreate
            {
                OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{userId}"
            };
            await Client.Groups[groupId].Owners.Ref.PostAsync(requestBody);
        }

        public async Task RemoveGroupOwnerAsync(string groupId, string userId)
        {
            await Client.Groups[groupId].Owners[userId].Ref.DeleteAsync();
        }

        /// <summary>
        /// Resolves an M365 (Unified) group's id and RecipientTypeDetails-equivalent classification
        /// by mail nickname / mail / id, mirroring Resolve-GroupOperationContext's M365 branch.
        /// Returns null if not found or not a Unified (M365) group.
        /// </summary>
        public async Task<string?> ResolveM365GroupIdAsync(string groupIdentity)
        {
            try
            {
                var response = await Client.Groups.GetAsync(config =>
                {
                    config.QueryParameters.Filter = $"mail eq '{groupIdentity}' or mailNickname eq '{groupIdentity}' or id eq '{groupIdentity}'";
                    config.QueryParameters.Select = new[] { "id", "groupTypes", "mail" };
                    config.Headers.Add("ConsistencyLevel", "eventual");
                    config.QueryParameters.Count = true;
                });

                var group = response?.Value?.FirstOrDefault(g => g.GroupTypes != null && g.GroupTypes.Contains("Unified"));
                return group?.Id;
            }
            catch (ODataError odataEx)
            {
                Logger.Log($"Graph group lookup failed for '{groupIdentity}': {odataEx.Error?.Code} - {odataEx.Error?.Message}", LogType.Error);
                return null;
            }
        }

        /// <summary>
        /// Finds the Bookings license group by name (Settings.LicenseGroups.Bookings.GroupName)
        /// and returns its group id, used by BookingsService to add users to the license group.
        /// </summary>
        public async Task<string?> GetGroupIdByNameAsync(string groupName)
        {
            try
            {
                var response = await Client.Groups.GetAsync(config =>
                {
                    config.QueryParameters.Filter = $"displayName eq '{groupName}'";
                    config.QueryParameters.Select = new[] { "id", "displayName" };
                });
                return response?.Value?.FirstOrDefault()?.Id;
            }
            catch (ODataError odataEx)
            {
                Logger.Log($"Graph group lookup failed for '{groupName}': {odataEx.Error?.Code} - {odataEx.Error?.Message}", LogType.Error);
                return null;
            }
        }

        /// <summary>
        /// Provisions a Microsoft Teams team on top of an existing Microsoft 365 Group, mirroring the
        /// "Create a team for this group" option in the Microsoft 365 admin center's group creation
        /// wizard (PUT /groups/{id}/team). The group must already exist before this call.
        /// </summary>
        public async Task CreateTeamFromGroupAsync(string groupId)
        {
            try
            {
                await Client.Groups[groupId].Team.PutAsync(new Team());
            }
            catch (ODataError odataEx)
            {
                Logger.Log($"Failed to create Team for group '{groupId}': {odataEx.Error?.Code} - {odataEx.Error?.Message}", LogType.Error);
                throw;
            }
        }

        public async Task<GraphUserRecord?> FindUserAsync(string upn, string samAccountName)
        {
            var directByUpn = await GetUserByIdAsync(upn);
            if (directByUpn != null)
            {
                return directByUpn;
            }

            var directBySam = await GetUserByIdAsync(samAccountName);
            if (directBySam != null)
            {
                return directBySam;
            }

            var escapedSam = EscapeFilterValue(samAccountName);
            var escapedUpn = EscapeFilterValue(upn);

            var bySam = await QueryFirstUserAsync($"onPremisesSamAccountName eq '{escapedSam}'");
            if (bySam != null)
            {
                return bySam;
            }

            return await QueryFirstUserAsync($"userPrincipalName eq '{escapedUpn}'");
        }

        public async Task<IReadOnlyList<GraphUserRecord>> FindDeletedUsersAsync(string upn, string samAccountName)
        {
            var matches = new List<GraphUserRecord>();
            var nextUrl = "https://graph.microsoft.com/v1.0/directory/deletedItems/microsoft.graph.user?$select=id,userPrincipalName,onPremisesSamAccountName,mail";
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                using var request = await CreateRequestAsync(HttpMethod.Get, nextUrl);
                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"Graph deleted-user query failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in values.EnumerateArray())
                    {
                        var candidate = ToGraphUserRecord(item);
                        if (candidate == null)
                        {
                            continue;
                        }

                        if (RetentionRules.IsDeletedUserMatch(candidate.Id, candidate.UserPrincipalName, candidate.OnPremisesSamAccountName, candidate.Mail, upn, samAccountName))
                        {
                            matches.Add(candidate);
                        }
                    }
                }

                nextUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                    ? next.GetString()
                    : null;
            }

            return matches;
        }

        public async Task RestoreDeletedUserAsync(string deletedDirectoryObjectId)
        {
            using var request = await CreateRequestAsync(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/directory/deletedItems/{deletedDirectoryObjectId}/restore");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Graph restore failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }
        }

        public async Task<HashSet<string>> GetUserMemberGroupIdsAsync(string userId)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextUrl = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}/memberOf?$select=id";

            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                using var request = await CreateRequestAsync(HttpMethod.Get, nextUrl);
                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"Graph memberOf query failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in values.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idElement))
                        {
                            var id = idElement.GetString();
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                ids.Add(id);
                            }
                        }
                    }
                }

                nextUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                    ? next.GetString()
                    : null;
            }

            return ids;
        }

        public async Task ResetPasswordAndDisableSignInAsync(string userId, string newPassword)
        {
            var payload = new
            {
                accountEnabled = false,
                passwordProfile = new
                {
                    forceChangePasswordNextSignIn = false,
                    password = newPassword
                }
            };

            using var request = await CreateRequestAsync(new HttpMethod("PATCH"), $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Graph password reset / disable failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }
        }

        private async Task<GraphUserRecord?> GetUserByIdAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return null;
            }

            using var request = await CreateRequestAsync(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}?$select=id,userPrincipalName,onPremisesSamAccountName,mail");
            using var response = await _httpClient.SendAsync(request);

            // The script treats a failed direct lookup as "not found"; Graph answers 400 for identifiers that are not a UPN or object ID.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Graph user lookup failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return ToGraphUserRecord(doc.RootElement);
        }

        private async Task<GraphUserRecord?> QueryFirstUserAsync(string filter)
        {
            using var request = await CreateRequestAsync(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users?$filter={Uri.EscapeDataString(filter)}&$count=true&$top=1&$select=id,userPrincipalName,onPremisesSamAccountName,mail");
            request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Graph user filter query failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var first = values.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Undefined ? null : ToGraphUserRecord(first);
        }

        private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url)
        {
            var token = await _authService.GetAccessTokenAsync(_scopes);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Unable to acquire Microsoft Graph access token.");
            }

            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        private static GraphUserRecord? ToGraphUserRecord(JsonElement element)
        {
            if (!element.TryGetProperty("id", out var idElement))
            {
                return null;
            }

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return new GraphUserRecord
            {
                Id = id,
                UserPrincipalName = element.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null,
                OnPremisesSamAccountName = element.TryGetProperty("onPremisesSamAccountName", out var sam) ? sam.GetString() : null,
                Mail = element.TryGetProperty("mail", out var mail) ? mail.GetString() : null
            };
        }

        private static bool EqualsCI(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string EscapeFilterValue(string value) => (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
    }
}
