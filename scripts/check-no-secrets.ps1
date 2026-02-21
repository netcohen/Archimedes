# check-no-secrets.ps1
# Scans repository for forbidden filenames and patterns that may contain secrets

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $scriptDir) { $scriptDir = Get-Location }
$root = Split-Path -Parent $scriptDir
if (-not $root -or -not (Test-Path $root)) { $root = Get-Location }

Write-Host "Scanning for secrets in: $root" -ForegroundColor Cyan

$forbiddenFiles = @(
    "*.pem",
    "*.key",
    "*.p12",
    "*.pfx",
    "*credentials*.json",
    "*secret*.json",
    "*-sa.json",
    "*server-key*",
    "firebase-adminsdk*.json",
    "service-account*.json",
    ".env.local",
    ".env.production"
)

$forbiddenPatterns = @(
    "AKIA[0-9A-Z]{16}",                          # AWS Access Key
    "sk_live_[a-zA-Z0-9]+",                      # Stripe live key
    "-----BEGIN PRIVATE KEY-----",
    "-----BEGIN RSA PRIVATE KEY-----",
    "-----BEGIN EC PRIVATE KEY-----",
    "firebase-adminsdk",
    '"private_key":\s*"-----BEGIN'
)

$ignoreDirectories = @(
    "node_modules",
    ".git",
    "bin",
    "obj",
    "dist",
    "build",
    ".gradle"
)

$ignoreFiles = @(
    "check-no-secrets.ps1",
    "security.md"
)

$foundIssues = @()

# Check for forbidden files
foreach ($pattern in $forbiddenFiles) {
    $files = Get-ChildItem -Path $root -Recurse -Filter $pattern -File -ErrorAction SilentlyContinue |
        Where-Object {
            $path = $_.FullName
            $skip = $false
            foreach ($ignore in $ignoreDirectories) {
                if ($path -match [regex]::Escape("\$ignore\")) { $skip = $true; break }
            }
            -not $skip
        }
    foreach ($f in $files) {
        $foundIssues += "FORBIDDEN FILE: $($f.FullName)"
    }
}

# Check for forbidden patterns in text files
$textExtensions = @("*.cs", "*.ts", "*.js", "*.json", "*.kt", "*.java", "*.xml", "*.md", "*.ps1", "*.sh", "*.yaml", "*.yml", "*.env*")
foreach ($ext in $textExtensions) {
    $files = Get-ChildItem -Path $root -Recurse -Filter $ext -File -ErrorAction SilentlyContinue |
        Where-Object {
            $path = $_.FullName
            $name = $_.Name
            $skip = $false
            foreach ($ignore in $ignoreDirectories) {
                if ($path -match [regex]::Escape("\$ignore\")) { $skip = $true; break }
            }
            if (-not $skip) {
                foreach ($ignoreFile in $ignoreFiles) {
                    if ($name -eq $ignoreFile) { $skip = $true; break }
                }
            }
            -not $skip
        }
    foreach ($f in $files) {
        $content = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $content) { continue }
        foreach ($pattern in $forbiddenPatterns) {
            if ($content -match $pattern) {
                $foundIssues += "FORBIDDEN PATTERN [$pattern] in: $($f.FullName)"
            }
        }
    }
}

if ($foundIssues.Count -gt 0) {
    Write-Host "`n=== SECRETS FOUND ===" -ForegroundColor Red
    foreach ($issue in $foundIssues) {
        Write-Host "  $issue" -ForegroundColor Yellow
    }
    Write-Host "`nFAIL: $($foundIssues.Count) issue(s) found. Do NOT commit." -ForegroundColor Red
    exit 1
} else {
    Write-Host "`nPASS: No secrets detected." -ForegroundColor Green
    exit 0
}
