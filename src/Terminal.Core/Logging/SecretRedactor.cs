using System.Text.RegularExpressions;

namespace Terminal.Logging;

internal static partial class SecretRedactor
{
    private const string Redacted = "[REDACTED]";

    [GeneratedRegex(@"sk-[A-Za-z0-9\-_]{20,}")]
    private static partial Regex SkToken();

    [GeneratedRegex(@"ghp_[A-Za-z0-9]{36}")]
    private static partial Regex GhpToken();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]{82}")]
    private static partial Regex GithubPat();

    [GeneratedRegex(@"Bearer\s+\S{8,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)Authorization:\s*[^\r\n]+")]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"-----BEGIN PRIVATE KEY-----.*?-----END PRIVATE KEY-----", RegexOptions.Singleline)]
    private static partial Regex PrivateKeyBlock();

    public static string Redact(string text)
    {
        text = SkToken().Replace(text, Redacted);
        text = GhpToken().Replace(text, Redacted);
        text = GithubPat().Replace(text, Redacted);
        text = BearerToken().Replace(text, Redacted);
        text = AuthorizationHeader().Replace(text, Redacted);
        text = PrivateKeyBlock().Replace(text, Redacted);
        return text;
    }
}
