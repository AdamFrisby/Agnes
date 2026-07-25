using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Agnes.Client;
using Agnes.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// Approval pairing end-to-end, across both halves of the flow that ship in different clients: a new
/// device asks to join (<see cref="PairingApproval"/>, the phone's connect screen) and an already-paired
/// one decides (<see cref="PairingManagement"/>, the desktop Devices pane and the phone's inbox).
///
/// The point of this mechanism is that it replaces a short typed code with something an attacker on the
/// network can't shortcut, so the properties worth pinning down are: nothing is issued without a human,
/// the digits both sides show are derived independently and match, and the deciding endpoints are shut
/// to anyone not already paired.
/// </summary>
public sealed class PairApprovalEndpointTests
{
    private const string ApproverToken = "test-token";

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Stands in for the first device, already paired by whatever bootstrap the operator used.
                    ["Agnes:PairingToken"] = ApproverToken,
                }));
            return base.CreateHost(builder);
        }
    }

    private static string NewDeviceKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public async Task A_request_waits_for_a_human_and_the_approval_hands_over_a_working_token()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        var publicKey = NewDeviceKey();
        var pending = await PairingApproval.RequestAsync("http://localhost", publicKey, "Pixel 9", http);

        // Nothing is issued yet, and the digits are the ones the approver will be shown.
        var beforeDecision = await PairingApproval.PollAsync("http://localhost", pending.RequestId, http);
        Assert.Equal(PairApprovalState.Pending, beforeDecision.State);
        Assert.Null(beforeDecision.Token);

        var waiting = await PairingManagement.PendingAsync("http://localhost", ApproverToken, http);
        var offered = Assert.Single(waiting);
        Assert.Equal("Pixel 9", offered.DeviceName);
        Assert.Equal(pending.VerificationCode, offered.VerificationCode);

        await PairingManagement.ApproveAsync("http://localhost", ApproverToken, offered.RequestId, http);

        var approved = await PairingApproval.PollAsync("http://localhost", pending.RequestId, http);
        Assert.Equal(PairApprovalState.Approved, approved.State);
        Assert.False(string.IsNullOrWhiteSpace(approved.Token));

        // The token is a real device token, not a placeholder: it opens a protected endpoint.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/devices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", approved.Token);
        using var devices = await http.SendAsync(request);
        devices.EnsureSuccessStatusCode();

        // And it was handed over exactly once — a second poll can't be replayed into a second token.
        var replay = await PairingApproval.PollAsync("http://localhost", pending.RequestId, http);
        Assert.Null(replay.Token);
    }

    [Fact]
    public async Task A_declined_device_never_receives_a_token()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        var pending = await PairingApproval.RequestAsync("http://localhost", NewDeviceKey(), "Unknown phone", http);
        await PairingManagement.DenyAsync("http://localhost", ApproverToken, pending.RequestId, http);

        var status = await PairingApproval.PollAsync("http://localhost", pending.RequestId, http);

        Assert.Equal(PairApprovalState.Denied, status.State);
        Assert.Null(status.Token);
        Assert.Empty(await PairingManagement.PendingAsync("http://localhost", ApproverToken, http));
    }

    [Fact]
    public async Task The_digits_are_derived_from_the_asking_devices_key_not_taken_on_trust()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        var publicKey = NewDeviceKey();
        var pending = await PairingApproval.RequestAsync("http://localhost", publicKey, "Pixel 9", http);

        // Both sides compute the same six digits from (key, request id) alone. If the requester simply
        // echoed whatever the host returned, comparing the two screens would prove nothing about which
        // device is actually being let in.
        Assert.Equal(PairVerification.Derive(publicKey, pending.RequestId), pending.VerificationCode);
        Assert.Matches("^[0-9]{6}$", pending.VerificationCode);
    }

    [Fact]
    public async Task Two_simultaneous_requests_are_told_apart_by_their_digits()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        // The attack this defends against: an attacker fires a request at the moment you start yours,
        // hoping you approve whichever card you see. The digits differ, so the comparison catches it.
        var mine = await PairingApproval.RequestAsync("http://localhost", NewDeviceKey(), "Pixel 9", http);
        var theirs = await PairingApproval.RequestAsync("http://localhost", NewDeviceKey(), "Pixel 9", http);

        Assert.NotEqual(mine.VerificationCode, theirs.VerificationCode);

        var waiting = await PairingManagement.PendingAsync("http://localhost", ApproverToken, http);
        Assert.Equal(2, waiting.Count);
        Assert.Equal(2, waiting.Select(w => w.VerificationCode).Distinct().Count());
    }

    [Fact]
    public async Task Deciding_requires_an_already_paired_device()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        var pending = await PairingApproval.RequestAsync("http://localhost", NewDeviceKey(), "Pixel 9", http);

        // A device that is merely *asking* to join must not be able to approve itself.
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/pair/pending"),
                     (HttpMethod.Post, "/pair/approve/" + pending.RequestId),
                     (HttpMethod.Post, "/pair/deny/" + pending.RequestId),
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Still waiting, untouched.
        var status = await PairingApproval.PollAsync("http://localhost", pending.RequestId, http);
        Assert.Equal(PairApprovalState.Pending, status.State);
    }

    [Fact]
    public async Task A_request_that_was_never_made_looks_exactly_like_one_that_expired()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        var status = await PairingApproval.PollAsync("http://localhost", "not-a-real-request", http);

        // Not "denied" and not an error: a guessing caller learns nothing about which ids exist.
        Assert.Equal(PairApprovalState.Unknown, status.State);
        Assert.Null(status.Token);
    }

    [Fact]
    public async Task A_request_without_a_key_is_rejected()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync("/pair/request",
            new PairApprovalRequest(string.Empty, "Pixel 9"));

        // The key is what the digits are derived from, so a keyless request could never be verified.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
