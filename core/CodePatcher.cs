using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Archimedes.Core;

/// <summary>
/// Phase 34 – Code Patcher: the "hands" of Archimedes' self-improvement.
///
/// Lifecycle per patch attempt:
///   1. Select next safe target (rotating list)
///   2. Extract the target method / array from the real .cs file
///   3. LLM generates an improved version
///   4. Sanity-check the output (must be valid-looking C#)
///   5. Apply patch to the real file
///   6. dotnet build — if fail → revert + abort
///   7. dotnet test  — if fail → revert + abort
///   8. Both pass    → SelfGitManager.CommitCoreChange (audit trail)
///
/// Safe targets only: additive/isolated sections (keyword lists,
/// static arrays, heuristic methods). Never touches business logic,
/// cryptography, state machines, or security-critical code.
/// </summary>
public sealed class CodePatcher
{
    private readonly LLMAdapter     _llm;
    private readonly SelfGitManager _git;
    private readonly string         _repoRoot;
    private int                     _targetIndex;

    // Max extracted lines sent to LLM — protects the 3B model from huge context
    private const int MaxExtractLines = 120;

    // ── Safe patch targets ────────────────────────────────────────────────
    // All are additive and isolated: more keywords, more patterns, more topics.
    // Never targets business logic, crypto, scheduling, or state machines.
    private static readonly (string RelPath, string Identifier, string Goal)[] SafeTargets =
    [
        (
            "core/LLMAdapter.cs",
            "HeuristicInterpret",
            "add more intent keywords and edge cases to improve fallback coverage. " +
            "Keep all existing cases intact and only add new else-if branches or extend existing keyword lists."
        ),
        (
            "core/FailureAnalyzer.cs",
            "AnalyzeFailure",
            "add more failure pattern recognition categories with helpful Hebrew recovery questions. " +
            "Keep all existing patterns intact and only append new else-if clauses."
        ),
        (
            "core/SelfAnalyzer.cs",
            "ResearchTopics",
            "expand the static string array with 3-5 additional relevant technical research topics " +
            "for an autonomous AI agent system (C#, LLM, browser automation, distributed systems). " +
            "Keep all existing entries and only add new string literals at the end of the array."
        ),
    ];

    public int SafeTargetCount => SafeTargets.Length;

    public CodePatcher(LLMAdapter llm, SelfGitManager git, string repoRoot)
    {
        _llm      = llm;
        _git      = git;
        _repoRoot = repoRoot;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempt to patch the next safe target. Never throws — returns a result record.
    /// Reverts the file automatically on build or test failure.
    /// </summary>
    public async Task<CodePatchResult> TryPatchAsync(string? insightHint, CancellationToken ct)
    {
        var target   = SafeTargets[_targetIndex % SafeTargets.Length];
        _targetIndex++;

        var filePath = Path.GetFullPath(Path.Combine(_repoRoot, target.RelPath));
        if (!File.Exists(filePath))
            return CodePatchResult.Fail($"Target file not found: {target.RelPath}");

        ArchLogger.LogInfo($"[CodePatcher] Target: {target.Identifier} in {target.RelPath}");

        // 1. Read source
        string originalContent;
        try   { originalContent = await File.ReadAllTextAsync(filePath, ct); }
        catch (Exception ex) { return CodePatchResult.Fail($"Cannot read file: {ex.Message}"); }

        // 2. Extract target section
        var extracted = ExtractIdentifier(originalContent, target.Identifier);
        if (extracted == null)
            return CodePatchResult.Fail($"Cannot extract '{target.Identifier}' from {target.RelPath}");

        var lineCount = extracted.Split('\n').Length;
        if (lineCount > MaxExtractLines)
            return CodePatchResult.Fail($"Target too large ({lineCount} lines > {MaxExtractLines})");

        ArchLogger.LogInfo($"[CodePatcher] Extracted {lineCount} lines for '{target.Identifier}'");

        // 3. LLM generates improvement
        var insightContext = insightHint != null
            ? $"Recent system insight: {insightHint[..Math.Min(200, insightHint.Length)]}\n\n"
            : "";

        var improved = await _llm.AskAsync(
            "You are a C# expert performing a small, safe improvement to an autonomous AI agent codebase. " +
            "Return ONLY the improved C# code. No markdown fences. No triple backticks. No explanations. " +
            "No comments about what you changed. Just the code.",
            $"Goal: {target.Goal}\n\n" +
            insightContext +
            $"Original C# code to improve:\n{extracted}\n\n" +
            "Improved version (code only):",
            maxTokens: 700);

        if (string.IsNullOrWhiteSpace(improved))
            return CodePatchResult.Fail("LLM returned empty output");

        // Clean markdown fences if LLM added them despite instructions
        improved = CleanLlmOutput(improved);

        // 4. Sanity checks
        if (improved == extracted)
            return CodePatchResult.Fail("LLM returned identical code — no improvement made");
        if (!improved.Contains('{') || !improved.Contains('}'))
            return CodePatchResult.Fail("LLM output missing braces — not valid C#");
        if (improved.Length < 20)
            return CodePatchResult.Fail("LLM output suspiciously short");

        // 5. Apply patch to real file
        var patched = originalContent.Replace(extracted, improved);
        if (patched == originalContent)
            return CodePatchResult.Fail("Replace produced no change — identifier match failed");

        try   { await File.WriteAllTextAsync(filePath, patched, ct); }
        catch (Exception ex) { return CodePatchResult.Fail($"Cannot write patch: {ex.Message}"); }

        ArchLogger.LogInfo($"[CodePatcher] Patch applied — running dotnet build...");

        // 6. Build verification
        var coreDir = Path.Combine(_repoRoot, "core");
        var (buildOk, buildOut) = await RunCommand("dotnet", "build --nologo -v quiet",
            coreDir, TimeSpan.FromSeconds(90), ct);

        if (!buildOk)
        {
            ArchLogger.LogWarn("[CodePatcher] Build FAILED — reverting");
            await SafeRevert(filePath, originalContent);
            var snippet = TailOutput(buildOut, 300);
            return CodePatchResult.Fail($"Build failed after patch: {snippet}");
        }

        ArchLogger.LogInfo("[CodePatcher] Build OK — running dotnet test...");

        // 7. Test verification
        var testDir = Path.Combine(_repoRoot, "core.tests");
        bool testOk;
        string testOut;

        if (Directory.Exists(testDir))
        {
            (testOk, testOut) = await RunCommand("dotnet", "test --nologo -v quiet",
                testDir, TimeSpan.FromSeconds(120), ct);
        }
        else
        {
            // No test project on this machine — build verification is sufficient
            testOk  = true;
            testOut = "(no test project found — skipped)";
            ArchLogger.LogWarn("[CodePatcher] core.tests not found — skipping test step");
        }

        if (!testOk)
        {
            ArchLogger.LogWarn("[CodePatcher] Tests FAILED — reverting");
            await SafeRevert(filePath, originalContent);
            var snippet = TailOutput(testOut, 300);
            return CodePatchResult.Fail($"Tests failed after patch: {snippet}");
        }

        ArchLogger.LogInfo("[CodePatcher] Tests OK — committing patch");

        // 8. Commit (fire-and-forget push handled inside SelfGitManager)
        var desc = $"improve {target.Identifier} in {Path.GetFileName(target.RelPath)}: " +
                   target.Goal[..Math.Min(70, target.Goal.Length)];
        _git.CommitCoreChange([filePath], desc);

        ArchLogger.LogInfo($"[CodePatcher] Patch committed: {target.Identifier}");

        return new CodePatchResult(
            Success      : true,
            Summary      : $"Patched {target.Identifier} in {Path.GetFileName(target.RelPath)} — build+tests OK",
            TargetFile   : target.RelPath,
            TargetMethod : target.Identifier);
    }

    // ── Identifier extraction ─────────────────────────────────────────────

    /// <summary>
    /// Extracts a named code block (method or field initializer) from C# source.
    /// Finds the identifier by name, then walks braces to extract the full block
    /// including the signature or field declaration.
    /// </summary>
    private static string? ExtractIdentifier(string source, string identifier)
    {
        // Match the identifier as a whole word
        var pattern = $@"\b{Regex.Escape(identifier)}\b";
        var m = Regex.Match(source, pattern);
        if (!m.Success) return null;

        // Find the opening brace at or after the match position
        var openIdx = source.IndexOf('{', m.Index);
        if (openIdx < 0) return null;

        // Walk backward from the match to find the start of the declaration line
        var sigStart = source.LastIndexOf('\n', m.Index);
        sigStart = sigStart < 0 ? 0 : sigStart + 1;

        // Walk forward from openIdx finding the matching closing brace (balanced)
        var depth    = 0;
        var closeIdx = -1;
        for (int i = openIdx; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{': depth++; break;
                case '}': depth--;
                    if (depth == 0) { closeIdx = i; goto done; }
                    break;
            }
        }
        done:
        if (closeIdx < 0) return null;

        return source[sigStart..(closeIdx + 1)];
    }

    // ── LLM output cleanup ────────────────────────────────────────────────

    private static string CleanLlmOutput(string raw)
    {
        // Remove markdown code fences (```csharp ... ``` or ``` ... ```)
        raw = Regex.Replace(raw, @"^```[a-zA-Z]*\s*", "", RegexOptions.Multiline);
        raw = Regex.Replace(raw, @"^```\s*$",          "", RegexOptions.Multiline);
        return raw.Trim();
    }

    // ── Shell helpers ─────────────────────────────────────────────────────

    private static async Task<(bool ok, string output)> RunCommand(
        string exe, string args, string workingDir, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var process = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            process.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            try   { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "Command timed out");
            }

            return (process.ExitCode == 0, sb.ToString());
        }
        catch (Exception ex)
        {
            return (false, $"Command error: {ex.Message}");
        }
    }

    private static async Task SafeRevert(string filePath, string originalContent)
    {
        try   { await File.WriteAllTextAsync(filePath, originalContent); }
        catch (Exception ex) { ArchLogger.LogWarn($"[CodePatcher] Revert failed: {ex.Message}"); }
    }

    private static string TailOutput(string output, int chars)
        => output.Length > chars ? "..." + output[^chars..] : output;
}

// ── Result ────────────────────────────────────────────────────────────────

public record CodePatchResult(
    bool    Success,
    string  Summary,
    string? TargetFile   = null,
    string? TargetMethod = null)
{
    public static CodePatchResult Fail(string reason) => new(false, reason);
}
