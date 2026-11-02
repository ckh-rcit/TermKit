using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public sealed class ServiceNowTaskDetails
    {
        public string TicketNumber { get; set; } = string.Empty;
        public string ShortDescription { get; set; } = string.Empty;
        public string? TermUser { get; set; }
        public string? DelegateUser { get; set; }
        public string? DelegateName { get; set; }
        public DateTime? RetentionEndDate { get; set; }
        public bool? RetainEmail { get; set; }
        public List<string> Warnings { get; } = new();
    }

    public sealed class ServiceNowLookupResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public ServiceNowTaskDetails? Details { get; set; }
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

        private static readonly Regex TicketPattern = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
        private static readonly Regex IdentifierPattern = new(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.Compiled);
        private static readonly Regex SysIdPattern = new(@"\A[a-fA-F0-9]{32}\z", RegexOptions.Compiled);

        /// <summary>
        /// Reads the task's requested-item catalog variables with the service account and maps them to the
        /// retention form fields. Read-only: nothing in ServiceNow is changed.
        /// </summary>
        public async Task<ServiceNowLookupResult> LookupTaskAsync(string ticketNumber)
        {
            static ServiceNowLookupResult Fail(string message) => new() { Success = false, Message = message };

            if (!_config.Enabled) return Fail("ServiceNow integration is not enabled in config.");
            if (string.IsNullOrWhiteSpace(_config.InstanceUrl) || string.IsNullOrWhiteSpace(_config.KeyVaultUrl) ||
                string.IsNullOrWhiteSpace(_config.UsernameSecretName) || string.IsNullOrWhiteSpace(_config.PasswordSecretName))
                return Fail("ServiceNow config field is missing or empty.");
            ticketNumber = (ticketNumber ?? string.Empty).Trim();
            if (!TicketPattern.IsMatch(ticketNumber)) return Fail("Task/ticket number must contain only letters, digits, '-' or '_'.");

            string credentials;
            try
            {
                var snUser = await GetKeyVaultSecretAsync(_config.KeyVaultUrl, _config.UsernameSecretName);
                var snPass = await GetKeyVaultSecretAsync(_config.KeyVaultUrl, _config.PasswordSecretName);
                credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snUser}:{snPass}"));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to retrieve ServiceNow credentials from Key Vault: {ex.Message}");
            }

            var instanceUrl = _config.InstanceUrl.TrimEnd('/');
            var table = string.IsNullOrWhiteSpace(_config.Table) ? "task" : _config.Table;
            var numField = string.IsNullOrWhiteSpace(_config.TicketNumberField) ? "number" : _config.TicketNumberField;

            try
            {
                var query = Uri.EscapeDataString($"{numField}={ticketNumber}");
                using var head = await GetJsonAsync($"{instanceUrl}/api/now/table/{table}?sysparm_query={query}&sysparm_fields=sys_id,sys_class_name&sysparm_limit=1", credentials);
                if (!head.RootElement.TryGetProperty("result", out var found) || found.GetArrayLength() == 0)
                    return Fail($"ServiceNow ticket '{ticketNumber}' not found in table '{table}'.");
                var sysId = GetFieldValue(found[0], "sys_id");
                var sysClass = GetFieldValue(found[0], "sys_class_name");
                if (!SysIdPattern.IsMatch(sysId) || !IdentifierPattern.IsMatch(sysClass))
                    return Fail($"ServiceNow ticket '{ticketNumber}' did not return a usable record.");

                using var task = await GetJsonAsync($"{instanceUrl}/api/now/table/{sysClass}/{sysId}?sysparm_fields=number,short_description,request_item", credentials);
                var taskRecord = task.RootElement.GetProperty("result");
                var requestItem = GetFieldValue(taskRecord, "request_item");
                if (!SysIdPattern.IsMatch(requestItem))
                    return Fail($"'{ticketNumber}' has no requested item, so there are no catalog variables to read.");

                var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var variableQuery = Uri.EscapeDataString($"request_item={requestItem}");
                using (var rows = await GetJsonAsync($"{instanceUrl}/api/now/table/sc_item_option_mtom?sysparm_query={variableQuery}&sysparm_fields=sc_item_option.item_option_new.name,sc_item_option.value&sysparm_limit=1000", credentials))
                {
                    foreach (var row in rows.RootElement.GetProperty("result").EnumerateArray())
                    {
                        var name = GetFieldValue(row, "sc_item_option.item_option_new.name");
                        var value = GetFieldValue(row, "sc_item_option.value");
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value) && !variables.ContainsKey(name)) variables[name] = value.Trim();
                    }
                }

                var details = new ServiceNowTaskDetails { TicketNumber = ticketNumber, ShortDescription = GetFieldValue(taskRecord, "short_description") };
                variables.TryGetValue(_config.RetentionTermUserVariable, out var termUser);
                variables.TryGetValue(_config.RetentionDelegateVariable, out var delegateValue);
                variables.TryGetValue(_config.RetentionEndDateVariable, out var endDate);
                variables.TryGetValue(_config.RetentionFlagVariable, out var retainFlag);

                details.TermUser = string.IsNullOrWhiteSpace(termUser) ? null : termUser;
                if (details.TermUser == null) details.Warnings.Add($"Term user was not found (variable '{_config.RetentionTermUserVariable}').");

                if (!string.IsNullOrWhiteSpace(retainFlag))
                {
                    details.RetainEmail = retainFlag.Equals("yes", StringComparison.OrdinalIgnoreCase) || retainFlag.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                // Without retention the delegate and date are not needed, so their absence is not a problem.
                var retentionNeeded = details.RetainEmail != false;

                if (!string.IsNullOrWhiteSpace(delegateValue))
                {
                    if (SysIdPattern.IsMatch(delegateValue))
                    {
                        using var user = await GetJsonAsync($"{instanceUrl}/api/now/table/sys_user/{delegateValue}?sysparm_fields=email,user_name,name", credentials);
                        var userRecord = user.RootElement.GetProperty("result");
                        var email = GetFieldValue(userRecord, "email");
                        var userName = GetFieldValue(userRecord, "user_name");
                        details.DelegateUser = !string.IsNullOrWhiteSpace(email) ? email : (!string.IsNullOrWhiteSpace(userName) ? userName : null);
                        details.DelegateName = GetFieldValue(userRecord, "name");
                        if (details.DelegateUser == null) details.Warnings.Add("The delegate's ServiceNow user has no email or user name.");
                    }
                    else
                    {
                        details.DelegateUser = delegateValue;
                    }
                }
                else if (retentionNeeded)
                {
                    details.Warnings.Add($"Delegate was not found (variable '{_config.RetentionDelegateVariable}').");
                }

                if (!string.IsNullOrWhiteSpace(endDate))
                {
                    if (DateTime.TryParseExact(endDate, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        details.RetentionEndDate = parsed.Date;
                    else if (retentionNeeded)
                        details.Warnings.Add($"Retention end date '{endDate}' is not in a recognized format.");
                }
                else if (retentionNeeded)
                {
                    details.Warnings.Add($"Retention end date was not found (variable '{_config.RetentionEndDateVariable}').");
                }

                return new ServiceNowLookupResult { Success = true, Message = $"Read '{ticketNumber}' from ServiceNow.", Details = details };
            }
            catch (Exception ex)
            {
                return Fail($"Failed to read ServiceNow ticket '{ticketNumber}': {ex.Message}");
            }
        }

        private async Task<JsonDocument> GetJsonAsync(string url, string credentials)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request);
            await EnsureSuccessAsync(response);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
