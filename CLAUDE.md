# CLAUDE.md

## Subagent model policy

Use Sonnet subagents for routine or parallelizable work:
- codebase exploration
- searching for relevant files
- investigating configuration
- straightforward implementation
- tests and verification

Use Opus subagents only when the delegated task requires substantial reasoning or architectural decisions.
When spawning a subagent, explicitly select `sonnet` unless there is a reason to use another model.
