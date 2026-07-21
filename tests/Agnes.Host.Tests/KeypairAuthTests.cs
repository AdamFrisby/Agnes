using System.Security.Cryptography;
using System.Text;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class KeypairAuthTests : IDisposable
{
    private readonly string _keysFile = Path.Combine(Path.GetTempPath(), $"agnes-authkeys-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (File.Exists(_keysFile)) File.Delete(_keysFile);
    }

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static string Spki(ECDsa k) => Convert.ToBase64String(k.ExportSubjectPublicKeyInfo());
    private static string Sign(ECDsa k, string nonce) => Convert.ToBase64String(
        k.SignData(Encoding.UTF8.GetBytes(nonce), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    private KeypairAuth Auth(params ECDsa[] authorized)
    {
        File.WriteAllLines(_keysFile, authorized.Select(k => Spki(k) + " test-key"));
        return new KeypairAuth(new KeypairAuthOptions { Enabled = true, AuthorizedKeysFile = _keysFile });
    }

    [Fact]
    public void Authorized_key_with_a_valid_signature_is_accepted()
    {
        using var key = NewKey();
        var auth = Auth(key);
        Assert.True(auth.IsUsable);
        var nonce = auth.IssueChallenge();
        Assert.Equal("test-key", auth.Verify(Spki(key), nonce, Sign(key, nonce)));
    }

    [Fact]
    public void Unauthorized_key_is_rejected()
    {
        using var authorized = NewKey();
        using var attacker = NewKey();
        var auth = Auth(authorized);
        var nonce = auth.IssueChallenge();
        Assert.Null(auth.Verify(Spki(attacker), nonce, Sign(attacker, nonce)));
    }

    [Fact]
    public void A_signature_that_does_not_match_the_presented_key_is_rejected()
    {
        using var key = NewKey();
        using var other = NewKey();
        var auth = Auth(key, other); // both authorized
        var nonce = auth.IssueChallenge();
        // Present key's public key but sign with 'other' — the signature won't verify against the presented key.
        Assert.Null(auth.Verify(Spki(key), nonce, Sign(other, nonce)));
    }

    [Fact]
    public void A_nonce_is_single_use()
    {
        using var key = NewKey();
        var auth = Auth(key);
        var nonce = auth.IssueChallenge();
        Assert.NotNull(auth.Verify(Spki(key), nonce, Sign(key, nonce)));
        Assert.Null(auth.Verify(Spki(key), nonce, Sign(key, nonce))); // reused → rejected
    }

    [Fact]
    public void An_unknown_nonce_is_rejected()
    {
        using var key = NewKey();
        var auth = Auth(key);
        Assert.Null(auth.Verify(Spki(key), "not-a-real-nonce", Sign(key, "not-a-real-nonce")));
    }

    [Fact]
    public void Empty_or_comment_only_authorized_keys_is_not_usable()
    {
        File.WriteAllText(_keysFile, "# just a comment\n");
        var auth = new KeypairAuth(new KeypairAuthOptions { Enabled = true, AuthorizedKeysFile = _keysFile });
        Assert.False(auth.IsUsable);
    }
}
