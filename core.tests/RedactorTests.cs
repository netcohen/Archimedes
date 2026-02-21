using Archimedes.Core;

namespace Archimedes.Core.Tests;

public class RedactorTests
{
    [Fact]
    public void Redact_Password_ReplacesWithMetadata()
    {
        var input = "user logged in with password=secret123";
        var result = Redactor.Redact(input);

        Assert.DoesNotContain("secret123", result);
        Assert.Contains("[REDACTED", result);
        Assert.Contains("len=9", result);
    }

    [Fact]
    public void Redact_Token_ReplacesWithMetadata()
    {
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var result = Redactor.Redact(input);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", result);
        Assert.Contains("[REDACTED", result);
    }

    [Fact]
    public void Redact_ApiKey_ReplacesWithMetadata()
    {
        var input = "api_key=sk_live_abcd1234efgh5678";
        var result = Redactor.Redact(input);

        Assert.DoesNotContain("sk_live_abcd1234efgh5678", result);
        Assert.Contains("[REDACTED", result);
    }

    [Fact]
    public void Redact_OTP_ReplacesWithMetadata()
    {
        var input = "OTP=123456 sent to user";
        var result = Redactor.Redact(input);

        Assert.DoesNotContain("123456", result);
        Assert.Contains("[REDACTED", result);
    }

    [Fact]
    public void Redact_Cookie_ReplacesWithMetadata()
    {
        var input = "session_id=abc123def456ghi789";
        var result = Redactor.Redact(input);

        Assert.DoesNotContain("abc123def456ghi789", result);
        Assert.Contains("[REDACTED", result);
    }

    [Fact]
    public void Redact_PrivateKey_ReplacesWithMetadata()
    {
        var input = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA...\n-----END RSA PRIVATE KEY-----";
        var result = Redactor.Redact(input);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", result);
        Assert.Contains("[REDACTED", result);
    }

    [Fact]
    public void Redact_PreservesNonSensitiveText()
    {
        var input = "User John logged in from IP 192.168.1.1";
        var result = Redactor.Redact(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void RedactPayload_ReturnsMetadataOnly()
    {
        var payload = "This is sensitive payload with password=secret123";
        var result = Redactor.RedactPayload(payload);

        Assert.DoesNotContain("sensitive", result);
        Assert.DoesNotContain("secret123", result);
        Assert.Contains("[payload len=", result);
        Assert.Contains("hash=", result);
    }

    [Fact]
    public void RedactJson_RedactsSensitiveKeys()
    {
        var json = @"{""username"":""john"",""password"":""secret123"",""token"":""abc.def.ghi""}";
        var result = Redactor.RedactJson(json);

        Assert.DoesNotContain("secret123", result);
        Assert.DoesNotContain("abc.def.ghi", result);
        Assert.Contains("john", result);
        Assert.Contains("[REDACTED", result);
    }

    [Fact]
    public void SafeMetadata_ReturnsCorrectInfo()
    {
        var payload = "test payload";
        var meta = Redactor.SafeMetadata(payload);

        Assert.Equal(12, meta["length"]);
        Assert.True((bool)meta["hasContent"]);
        Assert.IsType<string>(meta["hash"]);
        Assert.Equal(8, ((string)meta["hash"]).Length);
    }

    [Fact]
    public void Redact_NullInput_ReturnsEmpty()
    {
        var result = Redactor.Redact(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void RedactPayload_EmptyInput_ReturnsEmptyMarker()
    {
        var result = Redactor.RedactPayload("");
        Assert.Equal("[empty]", result);
    }
}
