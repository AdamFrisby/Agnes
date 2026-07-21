using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Agnes.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>Keypair sign-in end-to-end: challenge → sign with an authorized key → device token → REST.</summary>
public class KeypairAuthEndpointTests : IDisposable
{
    private readonly string _keysFile = Path.Combine(Path.GetTempPath(), $"agnes-authkeys-it-{Guid.NewGuid():n}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (File.Exists(_keysFile)) File.Delete(_keysFile);
    }

    private sealed class Factory(string keysFile) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:Auth:Pairing:Enabled"] = "false",
                    ["Agnes:Auth:Keypair:Enabled"] = "true",
                    ["Agnes:Auth:Keypair:AuthorizedKeysFile"] = keysFile,
                }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Signed_challenge_from_an_authorized_key_issues_a_usable_token()
    {
        var spki = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(_keysFile, spki + " my-laptop\n");

        using var factory = new Factory(_keysFile);
        using var http = factory.CreateClient();

        var methods = await http.GetFromJsonAsync<AuthMethods>("/auth/methods");
        Assert.True(methods!.Keypair);
        Assert.False(methods.Pairing);

        var challenge = await http.GetFromJsonAsync<KeypairChallenge>("/auth/keypair/challenge");
        Assert.NotNull(challenge);

        var signature = Convert.ToBase64String(_key.SignData(
            Encoding.UTF8.GetBytes(challenge!.Nonce), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        var ok = await http.PostAsJsonAsync("/auth/keypair",
            new KeypairAuthRequest(spki, challenge.Nonce, signature, "laptop"));
        ok.EnsureSuccessStatusCode();
        var paired = await ok.Content.ReadFromJsonAsync<PairResponse>();
        Assert.NotNull(paired);

        // The token authorizes a protected endpoint.
        var req = new HttpRequestMessage(HttpMethod.Get, "/devices");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", paired!.Token);
        using var devices = await http.SendAsync(req);
        devices.EnsureSuccessStatusCode();

        // A reused nonce is rejected (single-use).
        var replay = await http.PostAsJsonAsync("/auth/keypair",
            new KeypairAuthRequest(spki, challenge.Nonce, signature, "laptop"));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
    }
}
