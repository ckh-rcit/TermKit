using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Security;
using System.Threading.Tasks;

namespace TermKit.Services
{
    public sealed class OnPremAdUser
    {
        public string SamAccountName { get; set; } = string.Empty;
        public string? DistinguishedName { get; set; }
        public string? UserPrincipalName { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public long? MsExchRemoteRecipientType { get; set; }
        public long? MsExchRecipientTypeDetails { get; set; }
        public bool? MsExchHideFromAddressLists { get; set; }
    }

    /// <summary>
    /// Thin wrapper around the ActiveDirectory PowerShell module used by the legacy toolkit.
    /// Keeps AD auth state and executes cmdlets with that credential.
    /// </summary>
    public sealed class OnPremAdService
    {
        private PSCredential? _credential;
        private string? _connectedIdentity;

        public bool IsConnected => _credential != null;
        public string? ConnectedIdentity => _connectedIdentity;
        public string? LastError { get; private set; }

        /// <summary>True when the last connect failed because the domain rejected the credentials, not because a controller was unreachable.</summary>
        public bool LastFailureWasCredentialRejection { get; private set; }

        internal static bool IsCredentialRejection(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            foreach (var phrase in new[] { "rejected the client credentials", "logon failure", "password is incorrect", "bad password", "invalid credentials", "account has been locked", "account is locked" })
            {
                if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public async Task<bool> ConnectAsync(string adminSamAccountName, string adminPassword, ActiveDirectoryConfig config)
        {
            Disconnect();
            LastError = null;
            LastFailureWasCredentialRejection = false;
            if (string.IsNullOrWhiteSpace(adminSamAccountName) || string.IsNullOrWhiteSpace(adminPassword))
            {
                LastError = "Admin username and password are required.";
                return false;
            }

            var adminIdentity = $"{config.DomainNetbios}\\{adminSamAccountName.Trim()}";
            var secure = new SecureString();
            foreach (var c in adminPassword)
            {
                secure.AppendChar(c);
            }
            secure.MakeReadOnly();

            var testCredential = new PSCredential(adminIdentity, secure);

            var failures = new List<string>();
            foreach (var controller in GetDomainControllers(config))
            {
                try
                {
                    await ExecuteAsync(ps =>
                    {
                        ps.AddCommand("Import-Module")
                          .AddParameter("Name", "ActiveDirectory")
                          .AddParameter("ErrorAction", "Stop");
                        ps.Invoke();
                        if (ps.HadErrors) throw BuildPipelineException(ps);

                        ps.Commands.Clear();
                        ps.AddCommand("Get-ADDomain")
                          .AddParameter("Server", controller)
                          .AddParameter("Credential", testCredential)
                          .AddParameter("ErrorAction", "Stop");
                        ps.Invoke();
                        if (ps.HadErrors) throw BuildPipelineException(ps);
                    });

                    _credential = testCredential;
                    _connectedIdentity = adminIdentity;
                    return true;
                }
                catch (Exception exception)
                {
                    failures.Add($"{controller}: {exception.Message}");
                    Logger.Log($"AD connection failed on {controller}: {exception.Message}", LogType.Warning);
                    if (IsCredentialRejection(exception.Message))
                    {
                        // Retrying other controllers with bad credentials only burns lockout attempts.
                        LastFailureWasCredentialRejection = true;
                        break;
                    }
                }
            }

            secure.Dispose();
            LastError = failures.Count > 0 ? string.Join(Environment.NewLine, failures) : "No domain controllers are configured.";
            return false;
        }

        public void Disconnect()
        {
            _credential?.Password?.Dispose();
            _credential = null;
            _connectedIdentity = null;
        }

        public async Task<OnPremAdUser?> GetUserBySamAccountNameAsync(string samAccountName, ActiveDirectoryConfig config)
        {
            EnsureConnected();
            var safeSam = EscapeFilterValue(samAccountName);
            Exception? lastFailure = null;
            var completedRead = false;

            foreach (var controller in GetDomainControllers(config))
            {
                try
                {
                    var result = await ExecuteAsync(ps =>
                    {
                        ps.AddCommand("Import-Module")
                          .AddParameter("Name", "ActiveDirectory")
                          .AddParameter("ErrorAction", "Stop");
                        ps.Invoke();
                        if (ps.HadErrors) throw BuildPipelineException(ps);

                        ps.Commands.Clear();
                        ps.AddCommand("Get-ADUser")
                          .AddParameter("Filter", $"samAccountName -eq '{safeSam}'")
                          .AddParameter("Properties", new[]
                          {
                              "Enabled",
                              "DistinguishedName",
                              "Description",
                              "UserPrincipalName",
                              "msExchRemoteRecipientType",
                              "msExchRecipientTypeDetails",
                              "msExchHideFromAddressLists"
                          })
                          .AddParameter("Credential", _credential)
                          .AddParameter("Server", controller)
                          .AddParameter("ErrorAction", "SilentlyContinue");
                        var users = ps.Invoke();
                        if (ps.HadErrors) throw BuildPipelineException(ps);

                        if (users.Count == 0)
                        {
                            return null;
                        }

                        var u = users[0];
                        return new OnPremAdUser
                        {
                            SamAccountName = u.Properties["SamAccountName"]?.Value?.ToString() ?? samAccountName,
                            DistinguishedName = u.Properties["DistinguishedName"]?.Value?.ToString(),
                            UserPrincipalName = u.Properties["UserPrincipalName"]?.Value?.ToString(),
                            Description = u.Properties["Description"]?.Value?.ToString(),
                            Enabled = u.Properties["Enabled"]?.Value as bool? ?? false,
                            MsExchRemoteRecipientType = ToNullableInt64(u.Properties["msExchRemoteRecipientType"]?.Value),
                            MsExchRecipientTypeDetails = ToNullableInt64(u.Properties["msExchRecipientTypeDetails"]?.Value),
                            MsExchHideFromAddressLists = u.Properties["msExchHideFromAddressLists"]?.Value as bool?
                        };
                    });

                    completedRead = true;
                    if (result != null)
                    {
                        return result;
                    }
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                    Logger.Log($"AD lookup failed on {controller}: {exception.Message}", LogType.Warning);
                }
            }

            if (!completedRead) throw new InvalidOperationException("AD lookup could not be completed on any configured controller.", lastFailure);
            return null;
        }

        public async Task<string?> GetUserUpnBySamAccountNameAsync(string samAccountName, ActiveDirectoryConfig config)
        {
            var user = await GetUserBySamAccountNameAsync(samAccountName, config);
            return user?.UserPrincipalName;
        }

        /// <summary>Ports the script's Get-ADObject -IncludeDeletedObjects check that distinguishes a deleted account from a missing one.</summary>
        public async Task<bool> IsUserDeletedAsync(string samAccountName, ActiveDirectoryConfig config)
        {
            EnsureConnected();
            var safeSam = EscapeFilterValue(samAccountName);
            Exception? lastFailure = null;

            foreach (var controller in GetDomainControllers(config))
            {
                try
                {
                    return await ExecuteAsync(ps =>
                    {
                        ps.AddCommand("Import-Module")
                          .AddParameter("Name", "ActiveDirectory")
                          .AddParameter("ErrorAction", "Stop");
                        ps.Invoke();
                        if (ps.HadErrors) throw BuildPipelineException(ps);

                        ps.Commands.Clear();
                        ps.AddCommand("Get-ADObject")
                          .AddParameter("Filter", $"samAccountName -eq '{safeSam}'")
                          .AddParameter("IncludeDeletedObjects")
                          .AddParameter("Properties", new[] { "isDeleted" })
                          .AddParameter("Credential", _credential)
                          .AddParameter("Server", controller)
                          .AddParameter("ErrorAction", "Stop");
                        var objects = ps.Invoke();
                        if (ps.HadErrors) throw BuildPipelineException(ps);
                        return objects.Any(o => o.Properties["isDeleted"]?.Value is bool deleted && deleted);
                    });
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                    Logger.Log($"AD deleted-object lookup failed on {controller}: {exception.Message}", LogType.Warning);
                }
            }

            throw new InvalidOperationException("AD deleted-object lookup could not be completed on any configured controller.", lastFailure);
        }

        public async Task MoveUserToDisabledOuAsync(string distinguishedName, string disabledUsersOu, ActiveDirectoryConfig config)
        {
            EnsureConnected();
                        await ExecuteAgainstAnyControllerAsync(config, (ps, controller) =>
            {
                ps.AddCommand("Import-Module")
                  .AddParameter("Name", "ActiveDirectory")
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);

                ps.Commands.Clear();
                ps.AddCommand("Move-ADObject")
                  .AddParameter("Identity", distinguishedName)
                  .AddParameter("TargetPath", disabledUsersOu)
                  .AddParameter("Credential", _credential)
                                    .AddParameter("Server", controller)
                  .AddParameter("ErrorAction", "Stop");
            });
        }

        public async Task SetUserDescriptionAsync(string samAccountName, string description, ActiveDirectoryConfig config)
        {
            EnsureConnected();
                        await ExecuteAgainstAnyControllerAsync(config, (ps, controller) =>
            {
                ps.AddCommand("Import-Module")
                  .AddParameter("Name", "ActiveDirectory")
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);

                ps.Commands.Clear();
                ps.AddCommand("Set-ADUser")
                  .AddParameter("Identity", samAccountName)
                  .AddParameter("Description", description)
                  .AddParameter("Credential", _credential)
                                    .AddParameter("Server", controller)
                  .AddParameter("ErrorAction", "Stop");
            });
        }

        public async Task SetUserExpirationAsync(string samAccountName, DateTime expirationDate, ActiveDirectoryConfig config)
        {
            EnsureConnected();
                        await ExecuteAgainstAnyControllerAsync(config, (ps, controller) =>
            {
                ps.AddCommand("Import-Module")
                  .AddParameter("Name", "ActiveDirectory")
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);

                ps.Commands.Clear();
                ps.AddCommand("Clear-ADAccountExpiration")
                  .AddParameter("Identity", samAccountName)
                  .AddParameter("Credential", _credential)
                                    .AddParameter("Server", controller)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);

                ps.Commands.Clear();
                ps.AddCommand("Set-ADAccountExpiration")
                  .AddParameter("Identity", samAccountName)
                  .AddParameter("DateTime", expirationDate)
                  .AddParameter("Credential", _credential)
                                    .AddParameter("Server", controller)
                  .AddParameter("ErrorAction", "Stop");
            });
        }

        public async Task<bool> EnsureHybridSharedMailboxAttributesAsync(string samAccountName, ActiveDirectoryConfig config)
        {
            EnsureConnected();
            var user = await GetUserBySamAccountNameAsync(samAccountName, config);
            if (user == null)
            {
                throw new InvalidOperationException($"Could not find AD user '{samAccountName}'.");
            }

            var attributesAlreadyCorrect =
                user.MsExchRemoteRecipientType == 100 &&
                user.MsExchRecipientTypeDetails == 34359738368 &&
                user.MsExchHideFromAddressLists == true;

            if (attributesAlreadyCorrect)
            {
                return false;
            }

                        await ExecuteAgainstAnyControllerAsync(config, (ps, controller) =>
            {
                ps.AddCommand("Import-Module")
                  .AddParameter("Name", "ActiveDirectory")
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);

                ps.Commands.Clear();
                ps.AddCommand("Set-ADUser")
                  .AddParameter("Identity", samAccountName)
                  .AddParameter("Replace", new Dictionary<string, object>
                  {
                      ["msExchRemoteRecipientType"] = 100,
                      ["msExchRecipientTypeDetails"] = 34359738368,
                      ["msExchHideFromAddressLists"] = true
                  })
                  .AddParameter("Credential", _credential)
                                    .AddParameter("Server", controller)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors) throw BuildPipelineException(ps);

                ps.Commands.Clear();
                ps.AddCommand("Set-ADUser")
                  .AddParameter("Identity", samAccountName)
                  .AddParameter("Clear", "ShowInAddressBook")
                  .AddParameter("Credential", _credential)
                                    .AddParameter("Server", controller)
                  .AddParameter("ErrorAction", "Stop");
            });

            return true;
        }

        private void EnsureConnected()
        {
            if (_credential == null)
            {
                throw new InvalidOperationException("Not connected to Active Directory.");
            }
        }

        internal static System.Management.Automation.Runspaces.Runspace CreateRunspace()
        {
            var state = System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2();
            state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;
            state.Formats.Clear();
            state.Types.Clear();
            var runspace = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(new LoggerPSHost(), state);
            try
            {
                runspace.Open();
                return runspace;
            }
            catch { runspace.Dispose(); throw; }
        }

        private static async Task<T> ExecuteAsync<T>(Func<PowerShell, T> work)
        {
            return await Task.Run(() =>
            {
                using var runspace = CreateRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                return work(ps);
            });
        }

        private static async Task ExecuteAsync(Action<PowerShell> work)
        {
            await Task.Run(() =>
            {
                using var runspace = CreateRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                work(ps);
            });
        }

        private async Task ExecuteAgainstAnyControllerAsync(ActiveDirectoryConfig config, Action<PowerShell, string> configure)
        {
            Exception? last = null;
            foreach (var controller in GetDomainControllers(config))
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var runspace = CreateRunspace();
                        using var ps = PowerShell.Create();
                        ps.Runspace = runspace;
                        configure(ps, controller);
                        ps.Invoke();
                        if (ps.HadErrors) throw BuildPipelineException(ps);
                    });
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new InvalidOperationException("No domain controllers were available.");
        }

        private static IEnumerable<string> GetDomainControllers(ActiveDirectoryConfig config)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(config.PrimaryDomainController))
            {
                list.Add(config.PrimaryDomainController.Trim());
            }

            if (config.FallbackDomainControllers != null)
            {
                list.AddRange(config.FallbackDomainControllers.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
            }

            return list.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static Exception BuildPipelineException(PowerShell ps)
        {
            var message = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            return new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Unknown PowerShell pipeline error." : message);
        }

        private static long? ToNullableInt64(object? value)
        {
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt64(value);
            }
            catch
            {
                return null;
            }
        }

        private static string EscapeFilterValue(string value) => (value ?? string.Empty).Replace("'", "''");
    }
}
