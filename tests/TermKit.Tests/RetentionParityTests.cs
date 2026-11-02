using System.Net;
using System.Text;
using System.Text.Json;
using TermKit.Services;
using Xunit;

namespace TermKit.Tests;

public sealed class RequiresAdModuleFactAttribute : FactAttribute
{
    public RequiresAdModuleFactAttribute()
    {
        var module = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules", "ActiveDirectory");
        if (!Directory.Exists(module)) Skip = "RSAT ActiveDirectory module is not installed on this host.";
    }
}

public sealed class RetentionParityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TermKit-tests-" + Guid.NewGuid().ToString("N"));

    public RetentionParityTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private const string DeletedId = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("jdoe@contoso.org", null, null, true)]
    [InlineData("JDOE@CONTOSO.ORG", null, null, true)]
    [InlineData(DeletedId + "jdoe@contoso.org", null, null, false)]
    [InlineData("11111111222233334444555555555555jdoe@contoso.org", null, null, true)]
    [InlineData("other@contoso.org", "JDoe", null, true)]
    [InlineData("other@contoso.org", null, "jdoe@contoso.org", true)]
    [InlineData("other@contoso.org", "someone", "other@contoso.org", false)]
    [InlineData(null, null, null, false)]
    public void DeletedUserMatchingFollowsTheScriptRules(string? upn, string? sam, string? mail, bool expected) =>
        Assert.Equal(expected, RetentionRules.IsDeletedUserMatch(DeletedId, upn, sam, mail, "jdoe@contoso.org", "jdoe"));

    [Fact]
    public void GeneratedPasswordsMeetEveryComplexityClassAndDiffer()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var password = RetentionRules.NewRandomPassword();
            Assert.Equal(16, password.Length);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsDigit);
            Assert.True(password.Count(c => !char.IsLetterOrDigit(c)) >= 4);
            Assert.True(seen.Add(password));
        }
    }

    private static ToolConfig CompleteConfig() => new()
    {
        Settings = new SettingsConfig
        {
            ActiveDirectory = new ActiveDirectoryConfig
            {
                DomainNetbios = "CONTOSO",
                DomainDNS = "contoso.org",
                PrimaryDomainController = "dc01.contoso.org",
                DisabledUsersOU = "OU=Disabled,DC=contoso,DC=org"
            },
            LicenseGroups = new LicenseGroupsConfig
            {
                M365G3 = new LicenseGroupConfig { GroupId = "g3" },
                M365G5 = new LicenseGroupConfig { GroupId = "g5" },
                PrimaryLicenseGroup = "M365_G3"
            }
        }
    };

    [Fact]
    public void RetentionPreflightAcceptsACompleteConfiguration() => ConfigService.ValidateRetention(CompleteConfig());

    [Theory]
    [InlineData("DomainNetbios")]
    [InlineData("DomainDNS")]
    [InlineData("PrimaryDomainController")]
    [InlineData("DisabledUsersOU")]
    [InlineData("M365_G3.GroupId")]
    [InlineData("M365_G5.GroupId")]
    [InlineData("PrimaryLicenseGroup")]
    public void RetentionPreflightNamesEachMissingField(string field)
    {
        var config = CompleteConfig();
        var ad = config.Settings.ActiveDirectory!;
        var licenses = config.Settings.LicenseGroups;
        switch (field)
        {
            case "DomainNetbios": ad.DomainNetbios = ""; break;
            case "DomainDNS": ad.DomainDNS = " "; break;
            case "PrimaryDomainController": ad.PrimaryDomainController = ""; break;
            case "DisabledUsersOU": ad.DisabledUsersOU = ""; break;
            case "M365_G3.GroupId": licenses.M365G3 = null; break;
            case "M365_G5.GroupId": licenses.M365G5!.GroupId = null; break;
            case "PrimaryLicenseGroup": licenses.PrimaryLicenseGroup = null; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => ConfigService.ValidateRetention(config));
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void RetentionPreflightRejectsAnUnknownPrimaryLicenseGroup()
    {
        var config = CompleteConfig();
        config.Settings.LicenseGroups.PrimaryLicenseGroup = "M365_G9";
        Assert.Throws<InvalidOperationException>(() => ConfigService.ValidateRetention(config));
    }

    [Fact]
    public void LoadedConfigurationAlwaysRequestsTheScriptsGraphScopes()
    {
        File.WriteAllText(Path.Combine(_directory, "config.json"), """{"Settings":{"GraphApi":{"Scopes":["Directory.Read.All","group.readwrite.all"]}}}""");
        var scopes = ConfigService.Load(_directory).Settings.GraphApi.Scopes;
        Assert.Contains("Directory.Read.All", scopes);
        Assert.Contains("Directory.ReadWrite.All", scopes);
        Assert.Contains("User.ReadWrite.All", scopes);
        Assert.Single(scopes, scope => scope.Equals("Group.ReadWrite.All", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyGraphScopesStillFailValidation()
    {
        File.WriteAllText(Path.Combine(_directory, "config.json"), """{"Settings":{"GraphApi":{"Scopes":[]}}}""");
        Assert.Throws<InvalidOperationException>(() => ConfigService.Load(_directory));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
        public List<(HttpMethod Method, Uri Uri, string Body, string? Authorization)> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, body, request.Headers.Authorization?.Parameter));
            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ServiceNowService ServiceNow(StubHandler handler, ServiceNowConfig? config = null) =>
        new(config ?? new ServiceNowConfig
        {
            Enabled = true,
            InstanceUrl = "https://example.service-now.com/",
            KeyVaultUrl = "https://vault.vault.azure.net/",
            UsernameSecretName = "user-secret",
            PasswordSecretName = "pass-secret",
            AdditionalFields = new Dictionary<string, object> { ["close_code"] = "Solved" }
        }, new HttpClient(handler), name => Task.FromResult(name == "user-secret" ? "svc" : "pw"));

    [Fact]
    public async Task ServiceNowClosesTheResolvedRecordWithTheScriptsPayload()
    {
        var handler = new StubHandler((request, _) => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, """{"result":[{"sys_id":"abc123","number":"TASK0012345","sys_class_name":"sc_task"}]}""")
            : Json(HttpStatusCode.OK, """{"result":{}}"""));

        var result = await ServiceNow(handler).CloseTaskAsync("TASK0012345", "notes", "comments");

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("sysparm_query=number%3DTASK0012345", handler.Requests[0].Uri.Query);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("svc:pw")), handler.Requests[0].Authorization);
        Assert.Equal("PATCH", handler.Requests[1].Method.Method);
        Assert.Equal("/api/now/table/sc_task/abc123", handler.Requests[1].Uri.AbsolutePath);
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("3", payload.RootElement.GetProperty("state").GetString());
        Assert.Equal("notes", payload.RootElement.GetProperty("work_notes").GetString());
        Assert.Equal("comments", payload.RootElement.GetProperty("comments").GetString());
        Assert.Equal("Solved", payload.RootElement.GetProperty("close_code").GetString());
    }

    [Fact]
    public async Task ServiceNowReportsAMissingTicketWithoutUpdating()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"result":[]}"""));
        var result = await ServiceNow(handler).CloseTaskAsync("TASK1", "n", "c");
        Assert.False(result.Success);
        Assert.Contains("not found in table 'task'", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServiceNowNeverPatchesWithoutASysId()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"result":[{"number":"TASK1"}]}"""));
        var result = await ServiceNow(handler).CloseTaskAsync("TASK1", "n", "c");
        Assert.False(result.Success);
        Assert.Contains("did not return a sys_id", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServiceNowFailuresIncludeTheResponseBody()
    {
        var handler = new StubHandler((request, _) => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, """{"result":[{"sys_id":"abc123","sys_class_name":"sc_task"}]}""")
            : Json(HttpStatusCode.Forbidden, """{"error":{"message":"ACL denied"}}"""));
        var result = await ServiceNow(handler).CloseTaskAsync("TASK1", "n", "c");
        Assert.False(result.Success);
        Assert.Contains("Failed to update ServiceNow ticket 'TASK1'", result.Message);
        Assert.Contains("403", result.Message);
        Assert.Contains("ACL denied", result.Message);
    }

    [Theory]
    [InlineData("TASK1^ORnumber=TASK2")]
    [InlineData("TASK1&sysparm_limit=0")]
    [InlineData("")]
    public async Task ServiceNowRejectsTicketNumbersThatCouldChangeTheQuery(string ticket)
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"result":[]}"""));
        var result = await ServiceNow(handler).CloseTaskAsync(ticket, "n", "c");
        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServiceNowDoesNothingWhenDisabled()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"result":[]}"""));
        var result = await ServiceNow(handler, new ServiceNowConfig { Enabled = false }).CloseTaskAsync("TASK1", "n", "c");
        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }
}
