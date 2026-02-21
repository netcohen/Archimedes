# Archimedes – Security & Secrets Management

## Golden Rules

1. **NEVER commit secrets** – API keys, passwords, Firebase credentials, private keys
2. **NEVER log secrets** – even in debug mode
3. **NEVER hardcode secrets** – always use environment variables or secure storage

## Allowed Logging

| OK to log | NOT OK to log |
|-----------|---------------|
| Request paths, HTTP methods | Auth tokens, API keys |
| Task IDs, Job IDs | Passwords, private keys |
| Timestamps, durations | User PII (email, phone) |
| Error types, stack traces | Full request/response bodies with secrets |
| Envelope IDs | Decrypted envelope contents |

## Secrets Storage Locations

### Local Development

Store secrets in a local-only folder outside the repo:

```
C:\Users\<username>\.secrets\archimedes\
├── firebase-dev-sa.json    # Firebase service account (for FCM HTTP v1 + Firestore)
└── .env.local              # Local environment overrides
```

Then symlink or copy to the appropriate project folder, or set environment variables.

### Environment Files

| File | Purpose | Committed? |
|------|---------|------------|
| `.env.example` | Template with placeholder keys | ✅ Yes |
| `.env` | Local overrides | ❌ No |
| `.env.local` | Local secrets | ❌ No |
| `.env.production` | Production (if needed) | ❌ No |

### Net Layer

Copy `net/.env.example` to `net/.env.local` and fill in real values:

```bash
cp net/.env.example net/.env.local
```

## Forbidden Patterns in Repo

The script `scripts/check-no-secrets.ps1` scans for:

- Files: `*.pem`, `*.key`, `*.p12`, `*.pfx`, `*credentials*.json`, `*secret*`, `*-sa.json`, `*server-key*`
- Patterns: `AKIA`, `sk_live_`, `-----BEGIN PRIVATE KEY-----`, `-----BEGIN RSA PRIVATE KEY-----`
- Firebase: `firebase-adminsdk`, `service-account`

Run before committing:

```powershell
.\scripts\check-no-secrets.ps1
```

## Firebase / FCM

**Use FCM HTTP v1 API with OAuth2 (service account) only.** Legacy server keys are deprecated.

- **Service account JSON**: Store in `~/.secrets/archimedes/firebase-dev-sa.json`
- **Set env var**: `GOOGLE_APPLICATION_CREDENTIALS=C:\Users\<user>\.secrets\archimedes\firebase-dev-sa.json`
- **FCM HTTP v1**: Uses OAuth2 tokens from the service account (no separate key file needed)

## Android

- **google-services.json**: Required for Firebase; add to `.gitignore` if it contains sensitive project info
- **Keystore**: Never commit `*.keystore` files (already in `.gitignore`)

## Rotation

- Rotate keys immediately if accidentally committed
- Use `git filter-branch` or BFG Repo-Cleaner to remove from history
