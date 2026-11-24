using System.Text.RegularExpressions;

namespace BackupForge.Core.Logging;

/// <summary>
/// Removes secrets from any text that leaves the process (logs, status, e-mail, exception messages).
/// Known secret values are replaced verbatim; URL credentials and "password=…" style pairs are masked by pattern.
/// </summary>
public sealed partial class SecretMasker
{
    public const string Mask = "***";
    private const int MinSecretLength = 4;

    private readonly Lock _lock = new();
    private string[] _secrets = [];

    public void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinSecretLength) return;
        lock (_lock)
        {
            if (_secrets.Contains(secret, StringComparer.Ordinal)) return;
            // Longest first, so a secret that contains another one is masked as a whole.
            _secrets = [.. _secrets.Append(secret).OrderByDescending(s => s.Length)];
        }
    }

    public void RegisterAll(IEnumerable<string?> secrets)
    {
        foreach (var s in secrets) Register(s);
    }

    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<user>[^:/@\s]+):(?<pw>[^@\s/]+)@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pwd|secret|token|apikey|api_key|access_key|secret_access_key)\b\s*[=:]\s*)(?<val>""[^""]*""|'[^']*'|[^\s;,&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string[] secrets;
        lock (_lock) secrets = _secrets;
        var result = text;
        foreach (var s in secrets)
            if (result.Contains(s, StringComparison.Ordinal)) result = result.Replace(s, Mask, StringComparison.Ordinal);
        result = UrlCredentials().Replace(result, m => $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:{Mask}@");
        result = KeyValueSecret().Replace(result, m => m.Groups["key"].Value + Mask);
        return result;
    }
}
