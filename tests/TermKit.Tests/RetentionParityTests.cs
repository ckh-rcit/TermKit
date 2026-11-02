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

    private const string TaskId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RitmId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string UserId = "cccccccccccccccccccccccccccccccc";

    private static StubHandler LookupHandler(string variableRows, string userJson = """{"result":{"email":"LJames@contoso.org","user_name":"ljames","name":"Leroyna James"}}""", string taskJson = "") =>
        new((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/now/table/task") return Json(HttpStatusCode.OK, "{\"result\":[{\"sys_id\":\"" + TaskId + "\",\"sys_class_name\":\"sc_task\"}]}");
            if (path == $"/api/now/table/sc_task/{TaskId}")
                return Json(HttpStatusCode.OK, taskJson.Length > 0 ? taskJson : "{\"result\":{\"number\":\"TASK1\",\"short_description\":\"Termination: Retain Email - Jane Doe\",\"request_item\":{\"link\":\"x\",\"value\":\"" + RitmId + "\"}}}");
            if (path == "/api/now/table/sc_item_option_mtom") return Json(HttpStatusCode.OK, variableRows);
            if (path == $"/api/now/table/sys_user/{UserId}") return Json(HttpStatusCode.OK, userJson);
            return Json(HttpStatusCode.NotFound, """{"error":{"message":"unexpected"}}""");
        });

    private static string Row(string name, string value) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["sc_item_option.item_option_new.name"] = name,
        ["sc_item_option.value"] = value
    });

    // display_value=all returns each field as {display_value, value}.
    private static string ObjectRow(string name, string value) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["sc_item_option.item_option_new.name"] = new { display_value = name, value = name },
        ["sc_item_option.value"] = new { display_value = value, value }
    });

    private static string Rows(params string[] rows) => "{\"result\":[" + string.Join(",", rows) + "]}";

    [Fact]
    public async Task TaskLookupMapsCatalogVariablesToTheRetentionFields()
    {
        var rows = Rows(Row("v_employee_email", "JDoe@contoso.org"), ObjectRow("v_retention_user", UserId), Row("v_inbox_deletion_date", "2026-11-30"), Row("v_retention", "Yes"), Row("v_unrelated", "x"));
        var handler = LookupHandler(rows);

        var result = await ServiceNow(handler).LookupTaskAsync(" TASK1 ");

        Assert.True(result.Success, result.Message);
        var details = result.Details!;
        Assert.Equal("JDoe@contoso.org", details.TermUser);
        Assert.Equal("LJames@contoso.org", details.DelegateUser);
        Assert.Equal("Leroyna James", details.DelegateName);
        Assert.Equal(new DateTime(2026, 11, 30), details.RetentionEndDate);
        Assert.True(details.RetainEmail);
        Assert.Equal("Termination: Retain Email - Jane Doe", details.ShortDescription);
        Assert.Empty(details.Warnings);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains("number%3DTASK1", handler.Requests[0].Uri.Query);
        Assert.Contains($"request_item%3D{RitmId}", handler.Requests[2].Uri.Query);
    }

    [Fact]
    public async Task TaskLookupKeepsAPlainDelegateAddressAndWarnsAboutGaps()
    {
        var rows = Rows(Row("v_employee_email", "JDoe@contoso.org"), Row("v_retention_user", "ljames@contoso.org"), Row("v_inbox_deletion_date", "next month"), Row("v_retention", "No"));
        var handler = LookupHandler(rows);

        var result = await ServiceNow(handler).LookupTaskAsync("TASK1");

        Assert.True(result.Success, result.Message);
        Assert.Equal("ljames@contoso.org", result.Details!.DelegateUser);
        Assert.Null(result.Details.RetentionEndDate);
        Assert.False(result.Details.RetainEmail);
        Assert.Equal(2, result.Details.Warnings.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("sys_user"));
    }

    [Fact]
    public async Task TaskLookupReportsMissingVariablesWithoutInventingValues()
    {
        var handler = LookupHandler("""{"result":[]}""");
        var result = await ServiceNow(handler).LookupTaskAsync("TASK1");
        Assert.True(result.Success, result.Message);
        Assert.Null(result.Details!.TermUser);
        Assert.Null(result.Details.DelegateUser);
        Assert.Null(result.Details.RetentionEndDate);
        Assert.Equal(3, result.Details.Warnings.Count);
    }

    [Fact]
    public async Task TaskLookupHonorsConfiguredVariableNames()
    {
        var rows = Rows(Row("custom_user", "JDoe@contoso.org"));
        var config = new ServiceNowConfig
        {
            Enabled = true, InstanceUrl = "https://example.service-now.com/", KeyVaultUrl = "https://vault.vault.azure.net/",
            UsernameSecretName = "u", PasswordSecretName = "p", RetentionTermUserVariable = "custom_user"
        };
        var result = await ServiceNow(LookupHandler(rows), config).LookupTaskAsync("TASK1");
        Assert.Equal("JDoe@contoso.org", result.Details!.TermUser);
    }

    [Fact]
    public async Task TaskLookupFailsWhenTheTaskHasNoRequestedItem()
    {
        var handler = LookupHandler("""{"result":[]}""", taskJson: """{"result":{"number":"TASK1","short_description":"x","request_item":""}}""");
        var result = await ServiceNow(handler).LookupTaskAsync("TASK1");
        Assert.False(result.Success);
        Assert.Contains("no requested item", result.Message);
    }

    [Fact]
    public async Task TaskLookupRejectsUnsafeTicketNumbersAndSendsNothing()
    {
        var handler = LookupHandler("""{"result":[]}""");
        var result = await ServiceNow(handler).LookupTaskAsync("TASK1^ORnumber=TASK2");
        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }
}
