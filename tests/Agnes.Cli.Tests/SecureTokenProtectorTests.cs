using System.Security.Cryptography;
using Agnes.Cli;

namespace Agnes.Cli.Tests;

public sealed class SecureTokenProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var protector = new SecureTokenProtector("test-key-material");
        const string token = "abc123-secret-device-token";

        var sealedBlob = protector.Protect(token);

        Assert.NotEqual(token, sealedBlob);                 // never stored in the clear
        Assert.DoesNotContain(token, sealedBlob, StringComparison.Ordinal);
        Assert.Equal(token, protector.Unprotect(sealedBlob));
    }

    [Fact]
    public void A_blob_sealed_under_a_different_key_cannot_be_recovered()
    {
        var sealedBlob = new SecureTokenProtector("machine-a").Protect("secret");

        // Authenticated encryption: a different key (another machine/user) fails to decrypt rather than
        // silently returning garbage — the "a stolen file isn't a usable token elsewhere" property.
        Assert.Throws<AuthenticationTagMismatchException>(
            () => new SecureTokenProtector("machine-b").Unprotect(sealedBlob));
    }

    [Fact]
    public void Each_seal_uses_a_fresh_nonce_so_ciphertexts_differ()
    {
        var protector = new SecureTokenProtector("k");

        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }
}
