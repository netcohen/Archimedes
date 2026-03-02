namespace Archimedes.Core;

/// <summary>
/// Phase 24 — Failure Analyzer.
///
/// Analyzes a step failure and generates a concise Hebrew recovery question
/// using rule-based pattern matching. No LLM dependency — always fast and
/// deterministic. Covers the most common failure categories encountered in
/// browser automation and HTTP tasks.
/// </summary>
public static class FailureAnalyzer
{
    /// <summary>
    /// Returns a Hebrew recovery question based on the failure context.
    /// </summary>
    public static string Analyze(string failedStep, string failedAction, string errorMessage)
    {
        var err = errorMessage.ToLowerInvariant();

        // ── Authentication / Session ───────────────────────────────────────────
        if (err.Contains("session expired") || err.Contains("not logged in") ||
            err.Contains("unauthorized")    || err.Contains("401")          ||
            err.Contains("sign in")         || err.Contains("login required"))
            return $"הסשן פג תוקף בשלב \"{failedStep}\" — רוצה שאנסה שוב עם כניסה מחדש?";

        // ── Timeout / Network ──────────────────────────────────────────────────
        if (err.Contains("timeout")             || err.Contains("timed out")       ||
            err.Contains("504")                 || err.Contains("connection refused") ||
            err.Contains("unreachable")         || err.Contains("network error")    ||
            err.Contains("econnrefused"))
            return $"הפעולה עברה את מגבלת הזמן בשלב \"{failedStep}\" — רוצה שאנסה שוב?";

        // ── Not Found ──────────────────────────────────────────────────────────
        if (err.Contains("not found") || err.Contains("404") || err.Contains("no such"))
            return $"המשאב לא נמצא בשלב \"{failedStep}\" — ייתכן שה-URL או הנתיב השתנו. מה ברצונך לעשות?";

        // ── DOM / Browser Element ──────────────────────────────────────────────
        if (err.Contains("element")  || err.Contains("selector") ||
            err.Contains("locator")  || err.Contains("click failed") ||
            err.Contains("dom")      || err.Contains("xpath"))
            return $"האלמנט לא נמצא בדף בשלב \"{failedStep}\" — ממשק המשתמש ייתכן שהשתנה. רוצה שאנסה שוב?";

        // ── Permission / Forbidden ─────────────────────────────────────────────
        if (err.Contains("forbidden")    || err.Contains("403")         ||
            err.Contains("permission")   || err.Contains("access denied") ||
            err.Contains("not allowed"))
            return $"אין הרשאה לבצע את הפעולה בשלב \"{failedStep}\" — בדוק שיש לך גישה מתאימה.";

        // ── Rate Limit ─────────────────────────────────────────────────────────
        if (err.Contains("rate limit") || err.Contains("429") ||
            err.Contains("too many requests") || err.Contains("quota"))
            return $"חריגה ממגבלת קצב בשלב \"{failedStep}\" — רוצה שאמתין ואנסה שוב עוד מעט?";

        // ── Server Error ───────────────────────────────────────────────────────
        if (err.Contains("500") || err.Contains("502") || err.Contains("503") ||
            err.Contains("server error") || err.Contains("internal error"))
            return $"שגיאת שרת בשלב \"{failedStep}\" — רוצה שאנסה שוב?";

        // ── CAPTCHA / Bot Detection ────────────────────────────────────────────
        if (err.Contains("captcha") || err.Contains("bot detection") ||
            err.Contains("challenge") || err.Contains("cloudflare"))
            return $"זוהה כבוט בשלב \"{failedStep}\" — יש לפתור את ה-CAPTCHA ידנית. רוצה שאחכה?";

        // ── Default fallback ───────────────────────────────────────────────────
        var shortErr = errorMessage.Length > 80
            ? errorMessage[..80] + "…"
            : errorMessage;

        return $"המשימה נכשלה בשלב \"{failedStep}\": {shortErr}. מה ברצונך לעשות?";
    }
}
