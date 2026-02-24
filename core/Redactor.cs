using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Archimedes.Core;

public static class Redactor
{
    private static readonly Regex[] SensitivePatterns = new[]
    {
        new Regex(@"(password|passwd|pwd)\s*[:=]\s*[""']?([^""'\s,}]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(token|bearer|jwt|auth)\s*[:=]\s*[""']?([A-Za-z0-9_\-\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(api[_-]?key|apikey|secret[_-]?key|secretkey)\s*[:=]\s*[""']?([^""'\s,}]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(otp|pin|code)\s*[:=]\s*[""']?(\d{4,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(cookie|session[_-]?id|sessionid)\s*[:=]\s*[""']?([^""'\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(authorization|x-auth|x-api-key)\s*:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(private[_-]?key|privatekey)\s*[:=]\s*[""']?([^""']+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(credit[_-]?card|card[_-]?number|ccn)\s*[:=]\s*[""']?(\d[\d\s\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(ssn|social[_-]?security)\s*[:=]\s*[""']?(\d{3}[\-\s]?\d{2}[\-\s]?\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"-----BEGIN [A-Z ]+-----[\s\S]+?-----END [A-Z ]+-----", RegexOptions.Compiled),
        new Regex(@"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled),
    };

    private static readonly string[] SensitiveKeys = new[]
    {
        "password", "passwd", "pwd", "secret", "token", "bearer", "jwt",
        "apikey", "api_key", "api-key", "authorization", "auth",
        "cookie", "session", "sessionid", "session_id", "session-id",
        "otp", "pin", "code", "private_key", "privatekey", "private-key",
        "credit_card", "creditcard", "card_number", "ssn", "credentials"
    };

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        var result = input;

        foreach (var pattern in SensitivePatterns)
        {
            result = pattern.Replace(result, match =>
            {
                if (match.Groups.Count > 2)
                {
                    var key = match.Groups[1].Value;
                    var value = match.Groups[2].Value;
                    return $"{key}=[REDACTED len={value.Length} hash={Hash(value)}]";
                }
                return $"[REDACTED len={match.Value.Length} hash={Hash(match.Value)}]";
            });
        }

        return result;
    }

    public static string RedactPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return "[empty]";

        return $"[payload len={payload.Length} hash={Hash(payload)}]";
    }

    public static string RedactJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return json ?? string.Empty;

        var result = json;

        foreach (var key in SensitiveKeys)
        {
            var keyPattern = new Regex(
                $@"""{Regex.Escape(key)}""\s*:\s*(""[^""]*""|[0-9]+|true|false|null)",
                RegexOptions.IgnoreCase
            );
            result = keyPattern.Replace(result, match =>
            {
                var valueMatch = Regex.Match(match.Value, @":\s*(.+)$");
                var value = valueMatch.Success ? valueMatch.Groups[1].Value.Trim('"') : "";
                return $@"""{key}"":""[REDACTED len={value.Length}]""";
            });
        }

        return result;
    }

    public static Dictionary<string, object> SafeMetadata(string? payload)
    {
        return new Dictionary<string, object>
        {
            ["length"] = payload?.Length ?? 0,
            ["hash"] = Hash(payload),
            ["hasContent"] = !string.IsNullOrEmpty(payload)
        };
    }

    private static string Hash(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return "empty";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
