using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace TermKit.Services
{
    public class ServiceNowResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Ports Get-KeyVaultSecret and Close-ServiceNowTask: retrieves the ServiceNow service-account
    /// credentials from Azure Key Vault, looks up a ticket by number, and updates/closes it with
    /// work notes and additional comments. Uses interactive Azure AD auth (InteractiveBrowserCredential)
    /// for Key Vault access instead of the script's Connect-AzAccount/Az.Accounts dependency.
    /// </summary>
    public class ServiceNowService
    {
        private readonly ServiceNowConfig _config;
        private static readonly HttpClient SharedHttpClient = new();
        private readonly HttpClient _httpClient = SharedHttpClient;
        private readonly Func<string, Task<string>>? _readSecret;
        private readonly Func<string?> _tenantIdProvider;
        private InteractiveBrowserCredential? _keyVaultCredential;
        private string? _keyVaultTenant;

        public ServiceNowService(ServiceNowConfig config, Func<string?>? tenantIdProvider = null)
        {
            _config = config;
            _tenantIdProvider = tenantIdProvider ?? (() => null);
        }

        internal ServiceNowService(ServiceNowConfig config, HttpClient client, Func<string, Task<string>> readSecret)
        {
            _config = config;
            _httpClient = client;
            _readSecret = readSecret;
            _tenantIdProvider = () => null;
        }

        internal static InteractiveBrowserCredentialOptions CreateCredentialOptions(string? tenantId)
        {
            if (!Guid.TryParse(tenantId, out _)) throw new InvalidOperationException("Connect Graph and Exchange Online before using Key Vault.");
            return new InteractiveBrowserCredentialOptions { TenantId = tenantId, RedirectUri = new Uri("http://localhost") };
        }

        private async Task<string> GetKeyVaultSecretAsync(string vaultUrl, string secretName)
        {
            if (_readSecret != null) return await _readSecret(secretName);
            var tenantId = _tenantIdProvider();
            var options = CreateCredentialOptions(tenantId);
            if (!Uri.TryCreate(vaultUrl, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(vaultUri.UserInfo))
                throw new InvalidOperationException("Key Vault URL must use HTTPS without embedded credentials.");
            if (_keyVaultCredential == null || !string.Equals(tenantId, _keyVaultTenant, StringComparison.OrdinalIgnoreCase))
            {
                _keyVaultCredential = new InteractiveBrowserCredential(options);
                _keyVaultTenant = tenantId;
            }
            var client = new SecretClient(vaultUri, _keyVaultCredential);
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            var secret = await client.GetSecretAsync(secretName, cancellationToken: timeout.Token);
            return secret.Value.Value;
        }

        /// <summary>
        /// Validates that the configured ServiceNow instance URL and Key Vault secrets are reachable
        /// and correct, without making any destructive changes. Used by the Settings page to give the
        /// user immediate feedback when saving ServiceNow configuration.
        /// </summary>
        public async Task<ServiceNowResult> TestConnectionAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.InstanceUrl) || string.IsNullOrWhiteSpace(_config.KeyVaultUrl) ||
                string.IsNullOrWhiteSpace(_config.UsernameSecretName) || string.IsNullOrWhiteSpace(_config.PasswordSecretName))
            {
                return new ServiceNowResult { Success = false, Message = "Instance URL, Key Vault URL, Username Secret Name, and Password Secret Name are all required." };
            }

            if (!Uri.TryCreate(_config.InstanceUrl, UriKind.Absolute, out var instanceUri) || (instanceUri.Scheme != Uri.UriSchemeHttp && instanceUri.Scheme != Uri.UriSchemeHttps))
            {
                return new ServiceNowResult { Success = false, Message = $"Instance URL '{_config.InstanceUrl}' is not a valid absolute http(s) URL." };
            }

            if (!Uri.TryCreate(_config.KeyVaultUrl, UriKind.Absolute, out var keyVaultUri) || (keyVaultUri.Scheme != Uri.UriSchemeHttp && keyVaultUri.Scheme != Uri.UriSchemeHttps))
            {
                return new ServiceNowResult { Success = false, Message = $"Key Vault URL '{_config.KeyVaultUrl}' is not a valid absolute http(s) URL." };
            }

            string snUser, snPass;
            try
            {
                snUser = await GetKeyVaultSecretAsync(_config.KeyVaultUrl, _config.UsernameSecretName);
                snPass = await GetKeyVaultSecretAsync(_config.KeyVaultUrl, _config.PasswordSecretName);
            }
            catch (Exception ex)
            {
                return new ServiceNowResult { Success = false, Message = $"Failed to retrieve ServiceNow credentials from Key Vault '{_config.KeyVaultUrl}': {ex.Message}" };
            }

            if (string.IsNullOrEmpty(snUser) || string.IsNullOrEmpty(snPass))
            {
                return new ServiceNowResult { Success = false, Message = "Retrieved ServiceNow username or password secret is empty." };
            }

            var encodedCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snUser}:{snPass}"));
            var instanceUrl = _config.InstanceUrl.TrimEnd('/');
            var table = string.IsNullOrWhiteSpace(_config.Table) ? "task" : _config.Table;

            try
            {
                var testUrl = $"{instanceUrl}/api/now/table/{table}?sysparm_limit=1";
                using var request = new HttpRequestMessage(HttpMethod.Get, testUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCreds);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return new ServiceNowResult { Success = false, Message = $"ServiceNow rejected the credentials retrieved from Key Vault ({(int)response.StatusCode} {response.StatusCode})." };
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new ServiceNowResult { Success = false, Message = $"ServiceNow table '{table}' or instance URL '{instanceUrl}' could not be found (404)." };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new ServiceNowResult { Success = false, Message = $"ServiceNow returned an unexpected status code: {(int)response.StatusCode} {response.StatusCode}." };
                }

                return new ServiceNowResult { Success = true, Message = "Successfully connected to ServiceNow and validated credentials." };
            }
            catch (HttpRequestException ex)
            {
                return new ServiceNowResult { Success = false, Message = $"Could not reach ServiceNow instance '{instanceUrl}': {ex.Message}" };
            }
            catch (Exception ex)
            {
                return new ServiceNowResult { Success = false, Message = $"ServiceNow connection test failed: {ex.Message}" };
            }
        }

        public async Task<ServiceNowResult> CloseTaskAsync(string ticketNumber, string workNotes = "", string additionalComments = "")
        {
            if (!_config.Enabled)
            {
                return new ServiceNowResult { Success = false, Message = "ServiceNow integration is not enabled in config." };
            }

            if (string.IsNullOrWhiteSpace(_config.InstanceUrl) || string.IsNullOrWhiteSpace(_config.KeyVaultUrl) ||
                string.IsNullOrWhiteSpace(_config.UsernameSecretName) || string.IsNullOrWhiteSpace(_config.PasswordSecretName))
            {
                return new ServiceNowResult { Success = false, Message = "ServiceNow config field is missing or empty." };
            }

            // '^' and other query operators would otherwise change the ServiceNow encoded query.
            if (!System.Text.RegularExpressions.Regex.IsMatch(ticketNumber ?? string.Empty, @"\A[A-Za-z0-9_-]{1,64}\z"))
            {
                return new ServiceNowResult { Success = false, Message = "Task/ticket number must contain only letters, digits, '-' or '_'." };
            }
            string snUser, snPass;
            try
            {
                snUser = await GetKeyVaultSecretAsync(_config.KeyVaultUrl, _config.UsernameSecretName);
                snPass = await GetKeyVaultSecretAsync(_config.KeyVaultUrl, _config.PasswordSecretName);
            }
            catch (Exception ex)
            {
                return new ServiceNowResult { Success = false, Message = $"Failed to retrieve ServiceNow credentials from Key Vault: {ex.Message}" };
            }

            var encodedCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snUser}:{snPass}"));
            var instanceUrl = _config.InstanceUrl.TrimEnd('/');
            var table = string.IsNullOrWhiteSpace(_config.Table) ? "task" : _config.Table;
            var numField = string.IsNullOrWhiteSpace(_config.TicketNumberField) ? "number" : _config.TicketNumberField;

            string sysId, sysClass;
            try
            {
                var query = Uri.EscapeDataString($"{numField}={ticketNumber}");
                var lookupUrl = $"{instanceUrl}/api/now/table/{table}?sysparm_query={query}&sysparm_fields=sys_id,{numField},sys_class_name&sysparm_limit=1";
                using var lookupRequest = new HttpRequestMessage(HttpMethod.Get, lookupUrl);
                lookupRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCreds);
                lookupRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var lookupResponse = await _httpClient.SendAsync(lookupRequest);
                await EnsureSuccessAsync(lookupResponse);
                var lookupJson = await lookupResponse.Content.ReadAsStringAsync();
                using var lookupDoc = JsonDocument.Parse(lookupJson);

                if (!lookupDoc.RootElement.TryGetProperty("result", out var resultArray) || resultArray.GetArrayLength() == 0)
                {
                    return new ServiceNowResult { Success = false, Message = $"ServiceNow ticket '{ticketNumber}' not found in table '{table}'." };
                }

                var record = resultArray[0];
                sysId = GetFieldValue(record, "sys_id");
                if (string.IsNullOrWhiteSpace(sysId))
                {
                    return new ServiceNowResult { Success = false, Message = $"ServiceNow ticket '{ticketNumber}' did not return a sys_id." };
                }
                sysClass = GetFieldValue(record, "sys_class_name");
            }
            catch (Exception ex)
            {
                return new ServiceNowResult { Success = false, Message = $"Failed to update ServiceNow ticket '{ticketNumber}': {ex.Message}" };
            }

            var updateTable = !string.IsNullOrWhiteSpace(_config.UpdateTable) ? _config.UpdateTable : (!string.IsNullOrWhiteSpace(sysClass) ? sysClass : table);
            var stateField = string.IsNullOrWhiteSpace(_config.CloseStateField) ? "state" : _config.CloseStateField;
            var stateValue = string.IsNullOrWhiteSpace(_config.CloseStateValue) ? "3" : _config.CloseStateValue;
            var workNotesField = string.IsNullOrWhiteSpace(_config.WorkNotesField) ? "work_notes" : _config.WorkNotesField;
            var commentsField = string.IsNullOrWhiteSpace(_config.AdditionalCommentsField) ? "comments" : _config.AdditionalCommentsField;

            try
            {
                var updatePayload = new System.Collections.Generic.Dictionary<string, object?>
                {
                    [stateField] = stateValue,
                    [workNotesField] = workNotes,
                    [commentsField] = additionalComments
                };

                if (_config.AdditionalFields != null)
                {
                    foreach (var field in _config.AdditionalFields)
                    {
                        updatePayload[field.Key] = field.Value;
                    }
                }

                var updateUrl = $"{instanceUrl}/api/now/table/{updateTable}/{sysId}";
                using var updateRequest = new HttpRequestMessage(new HttpMethod("PATCH"), updateUrl);
                updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCreds);
                updateRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                updateRequest.Content = new StringContent(JsonSerializer.Serialize(updatePayload), Encoding.UTF8, "application/json");

                using var updateResponse = await _httpClient.SendAsync(updateRequest);
                await EnsureSuccessAsync(updateResponse);

                return new ServiceNowResult { Success = true, Message = $"ServiceNow ticket '{ticketNumber}' updated successfully on table '{updateTable}'." };
            }
            catch (Exception ex)
            {
                return new ServiceNowResult { Success = false, Message = $"Failed to update ServiceNow ticket '{ticketNumber}': {ex.Message}" };
            }
        }

        // The script surfaces the ServiceNow response body with HTTP failures; EnsureSuccessStatusCode would drop it.
        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > 500) body = body[..500];
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}" + (string.IsNullOrWhiteSpace(body) ? string.Empty : $" | Response: {body}"));
        }

        private static string GetFieldValue(JsonElement record, string fieldName)
        {
            if (!record.TryGetProperty(fieldName, out var field)) return string.Empty;
            if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("value", out var valueProp))
            {
                return valueProp.GetString() ?? string.Empty;
            }
            return field.GetString() ?? string.Empty;
        }
    }
}
