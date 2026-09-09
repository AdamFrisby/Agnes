using System.Net;
using System.Text;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The client half of device roles: the approval body actually carries the role a human chose, the
/// role a host reports actually reaches the caller, and a host too old to answer produces silence
/// rather than an error — a newly-paired device must never be told the host is broken because it
/// happens to predate <c>/devices/me</c>.
/// </summary>
public sealed class DeviceRoleClientTests
{
    [Fact]
    public async Task Approving_as_member_sends_the_role_in_the_body()
    {
        string? body = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://agnes.example/pair/approve/req-1", request.RequestUri!.ToString());
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        await PairingManagement.ApproveAsync("https://agnes.example/", "tok", "req-1", httpClient: client);

        Assert.Equal(DeviceRole.Member, Body<PairApprovalDecision>(body).Role);
    }

    [Fact]
    public async Task Approving_as_owner_sends_owner()
    {
        string? body = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        await PairingManagement.ApproveAsync(
            "https://agnes.example", "tok", "req-1", DeviceRole.Owner, httpClient: client);

        Assert.Equal(DeviceRole.Owner, Body<PairApprovalDecision>(body).Role);
    }

    [Fact]
    public async Task Me_parses_the_role_and_the_admission_kind()
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("https://agnes.example/devices/me", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK,
                """
                {"id":"dev-1","name":"Laptop","pairedAt":"2026-01-01T00:00:00+00:00","lastSeenAt":null,
                 "isCurrentDevice":true,"role":"Owner","kind":"keypair"}
                """);
        }));

        var me = await PairingManagement.MeAsync("https://agnes.example", "tok", client);

        Assert.NotNull(me);
        Assert.Equal(DeviceRole.Owner, me!.Role);
        Assert.Equal("keypair", me.Kind);
        Assert.True(me.IsCurrentDevice);
    }

    [Fact]
    public async Task Me_is_null_on_a_host_that_predates_the_endpoint()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await PairingManagement.MeAsync("https://agnes.example", "tok", client));
    }

    [Fact]
    public async Task Setting_a_role_puts_the_requested_role_and_reports_a_refusal()
    {
        string? body = null;
        using var ok = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("https://agnes.example/devices/dev-2/role", request.RequestUri!.ToString());
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        Assert.True(await PairingManagement.SetRoleAsync(
            "https://agnes.example", "tok", "dev-2", DeviceRole.Owner, ok));
        Assert.Equal(DeviceRole.Owner, Body<DeviceRoleRequest>(body).Role);

        using var refused = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.False(await PairingManagement.SetRoleAsync(
            "https://agnes.example", "tok", "dev-2", DeviceRole.Member, refused));
    }

    [Fact]
    public async Task Pruning_posts_the_window_in_days()
    {
        string? body = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://agnes.example/devices/prune", request.RequestUri!.ToString());
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        Assert.True(await PairingManagement.PruneAsync("https://agnes.example", "tok", 30, client));
        Assert.Equal(30, Body<DevicePruneRequest>(body).UnusedForDays);
    }

    [Fact]
    public void The_wording_a_member_sees_names_the_host_and_where_to_fix_it()
    {
        var desktop = DeviceRoleText.EmptyStateForMember("workshop");
        Assert.Contains("member on workshop", desktop, StringComparison.Ordinal);
        Assert.Contains("Settings › Devices", desktop, StringComparison.Ordinal);

        var mobile = DeviceRoleText.EmptyStateForMember("workshop", "More › Devices");
        Assert.Contains("More › Devices", mobile, StringComparison.Ordinal);

        Assert.Equal("Paired as owner", DeviceRoleText.Paired(DeviceRole.Owner));
        Assert.StartsWith("Paired as member", DeviceRoleText.Paired(DeviceRole.Member), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pairing", "paired with code")]
    [InlineData("approval", "vouched for by a device")]
    [InlineData("keypair", "authorized key")]
    [InlineData("github", "GitHub")]
    public void Admission_reads_as_words(string kind, string expected)
        => Assert.Equal(expected, DeviceRoleText.Admission(kind));

    [Fact]
    public void An_unknown_admission_kind_shows_nothing_rather_than_a_raw_token()
        => Assert.Null(DeviceRoleText.Admission("some-future-method"));

    /// <summary>Reads a request body back as the contract it claims to be — asserting on the parsed
    /// record rather than on substrings, so a change of enum encoding can't silently pass.</summary>
    private static T Body<T>(string? body)
        => System.Text.Json.JsonSerializer.Deserialize<T>(
               body ?? throw new InvalidOperationException("no body was sent"),
               new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
           ?? throw new InvalidOperationException("the body did not parse");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
