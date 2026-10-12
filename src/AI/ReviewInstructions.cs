namespace SourceGit.AI
{
    public enum ReviewMode
    {
        Chat,
        Bugs,
        Security,
        Exceptions,
        Tests,
        Patch,
        Explain,
    }

    public static class ReviewInstructions
    {
        public static string For(ReviewMode mode)
        {
            if (mode == ReviewMode.Chat)
                return "This request is ordinary conversation. Answer the question directly; a review format requested in an earlier turn does not apply unless the user asks for it again.";

            if (mode == ReviewMode.Explain)
                return "Explain the supplied changes in the user's language: intent, changed behavior, impact and limitations. Inspect related code with read-only tools when necessary. Distinguish facts from uncertainty; do not modify files.";

            if (mode == ReviewMode.Patch)
                return "Propose a minimal patch, do not modify anything. First call ReadFile for EVERY proposed target (also missing files), so a working-tree baseline exists before generation; historical content is not a writable baseline. Put complete valid unified diff in a fenced ```diff block, with diff --git, --- a/path and +++ b/path headers (or /dev/null for new/delete), accurate hunk counts, and context. Only UTF-8 regular text files are supported. Do not propose binary, symlink, submodule, rename or mode changes. Explain intent and verification; suggestions are never applied automatically.";
            var focus = mode switch
            {
                ReviewMode.Bugs => "Bugs and behavior regressions: correctness, state transitions, concurrency, compatibility and changed behavior.",
                ReviewMode.Security => "Security: trust boundaries, injection, authorization, secrets, path traversal and unsafe data handling.",
                ReviewMode.Exceptions => "Exceptional paths: null and empty inputs, boundary values, errors, cancellation, timeouts and resource cleanup.",
                ReviewMode.Tests => "Test plan: observable behavior, regressions, edge cases, failure and cancellation paths; give concrete input, steps and expected results.",
                _ => throw new System.ArgumentOutOfRangeException(nameof(mode)),
            };

            // 每轮替换系统契约，避免审查模式污染后续普通问答；格式错误的回复仍按 Markdown 展示。
            return $"""
                This request is a structured code review. Focus: {focus}
                Use the user's language and GitHub-flavored Markdown, not a JSON-only response.
                Report only actionable findings grounded in the supplied diff or inspected source. Inspect related code with read-only tools when necessary.
                Structure the report into Summary, Findings (highest severity first), Verification/Test plan, and Context limitations.
                For EACH finding include Severity (Critical/High/Medium/Low), Location (repository-relative file and verified line/range), Problem, Evidence, Impact, Recommendation, and Verification.
                Distinguish verified facts from uncertainty; never invent line numbers, evidence, executed tests or vulnerability claims. State 'location not verified' when needed.
                When no supported findings exist say so explicitly; do not manufacture issues to fill the format. Identify truncated, missing, historical or unread context and what remains unchecked.
                For a test plan give Preconditions, Input/Action, Expected result and Priority for each case, mapped to the relevant finding or behavior.
                You are read-only: do not change files, run arbitrary commands, stage, commit or push. Suggestions are not applied changes.
                """;
        }
    }
}
