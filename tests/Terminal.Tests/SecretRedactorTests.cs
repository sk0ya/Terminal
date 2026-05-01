using Terminal.Logging;

namespace Terminal.Tests;

public class SecretRedactorTests
{
    [Fact]
    public void Redact_OpenAiKey_IsRedacted()
    {
        string input = "export OPENAI_API_KEY=sk-abcdefghijklmnopqrstuvwxyz1234567890";
        string result = SecretRedactor.Redact(input);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz1234567890", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_GhpToken_IsRedacted()
    {
        string token = "ghp_" + new string('A', 36);
        string input = $"token={token}";
        string result = SecretRedactor.Redact(input);
        Assert.DoesNotContain(token, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_GithubPat_IsRedacted()
    {
        string token = "github_pat_" + new string('A', 82);
        string result = SecretRedactor.Redact(token);
        Assert.DoesNotContain(token, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_BearerToken_IsRedacted()
    {
        string input = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        string result = SecretRedactor.Redact(input);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_AuthorizationHeader_IsRedacted()
    {
        string input = "Authorization: token abc123def456";
        string result = SecretRedactor.Redact(input);
        Assert.DoesNotContain("abc123def456", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_PrivateKeyBlock_IsRedacted()
    {
        string input = "-----BEGIN PRIVATE KEY-----\nMIIEvAIBADAN...\n-----END PRIVATE KEY-----";
        string result = SecretRedactor.Redact(input);
        Assert.DoesNotContain("MIIEvAIBADAN", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_PlainText_IsUnchanged()
    {
        string input = "This is a normal message with no secrets.";
        string result = SecretRedactor.Redact(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Redact_ShortSkToken_IsNotRedacted()
    {
        string input = "sk-short";
        string result = SecretRedactor.Redact(input);
        Assert.Equal(input, result);
    }
}
