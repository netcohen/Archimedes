import * as crypto from "crypto";

const SENSITIVE_PATTERNS: RegExp[] = [
  /(password|passwd|pwd)\s*[:=]\s*["']?([^"'\s,}]+)/gi,
  /(token|bearer|jwt|auth)\s*[:=]\s*["']?([A-Za-z0-9_\-.]+)/gi,
  /(api[_-]?key|apikey|secret[_-]?key|secretkey)\s*[:=]\s*["']?([^"'\s,}]+)/gi,
  /(otp|pin|code)\s*[:=]\s*["']?(\d{4,8})/gi,
  /(cookie|session[_-]?id|sessionid)\s*[:=]\s*["']?([^"'\s,;]+)/gi,
  /(authorization|x-auth|x-api-key)\s*:\s*([^\r\n]+)/gi,
  /-----BEGIN [A-Z ]+-----[\s\S]+?-----END [A-Z ]+-----/g,
  /eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g,
];

const SENSITIVE_KEYS = [
  "password", "passwd", "pwd", "secret", "token", "bearer", "jwt",
  "apikey", "api_key", "api-key", "authorization", "auth",
  "cookie", "session", "sessionid", "session_id", "session-id",
  "otp", "pin", "code", "private_key", "privatekey", "private-key",
  "credentials"
];

function hash(input: string): string {
  if (!input) return "empty";
  return crypto.createHash("sha256").update(input).digest("hex").slice(0, 8);
}

export function redact(input: string | undefined | null): string {
  if (!input) return input ?? "";

  let result = input;

  for (const pattern of SENSITIVE_PATTERNS) {
    result = result.replace(pattern, (match, key, value) => {
      if (value) {
        return `${key}=[REDACTED len=${value.length} hash=${hash(value)}]`;
      }
      return `[REDACTED len=${match.length} hash=${hash(match)}]`;
    });
  }

  return result;
}

export function redactPayload(payload: string | undefined | null): string {
  if (!payload) return "[empty]";
  return `[payload len=${payload.length} hash=${hash(payload)}]`;
}

export function safeMetadata(payload: string | undefined | null): {
  length: number;
  hash: string;
  hasContent: boolean;
} {
  return {
    length: payload?.length ?? 0,
    hash: hash(payload ?? ""),
    hasContent: !!payload,
  };
}

export function safeLog(context: string, message: string): void {
  console.log(`[${context}] ${redact(message)}`);
}

export function safeLogPayload(context: string, payload: string | undefined | null): void {
  const meta = safeMetadata(payload);
  console.log(`[${context}] payload: len=${meta.length} hash=${meta.hash}`);
}
