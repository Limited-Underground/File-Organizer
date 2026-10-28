# Contributing and resuming development

Community modifications and pull requests are welcome under Apache 2.0. Intentionally submitted contributions follow LICENSE; no additional contributor agreement is currently required.

## Authority and continuity

Read README and docs/PROJECT_STATUS.md first. GitHub Projects will own live task scope, priority, dependencies and owner decisions once established. docs/ROADMAP.md is the initial publication draft, not a competing live tracker. Until then, the explicit foundation request authorizes local preparation only. Board status never grants implementation, publication, system, physical or release authority.

Before each task inspect the actual repository/ref, working changes, scope and retained task artifacts; assign appropriate subagents. Use clear outcomes and proportionate validation. No fixed six-section ceremony, daily PR limit or publication time window applies. Preserve dirty work, evidence and unrelated files. Recover routine tool failures without expanding scope. Do not access obsolete copies or another session's active workspace.

Publication requires explicit authorization for repository, destination, ref and applicable commit/push/PR/merge operations. Never force-push, rewrite history, bypass checks or publish private records. No automatic application implementation follows acceptance of these documents.

## Layout and coding

The planned layout is src/ for desktop and application/core modules, src/adapters/ for platform adapters, tests/ for unit/integration/fault tests, docs/ for contracts, tools/ for developer tooling, and ignored artifacts/ and tmp/ for generated and disposable output. ARCHITECTURE.md owns exact module names. Create directories when their first real file is needed; no placeholder code or empty projects are required.

Use modern C# on .NET 10, nullable reference types, explicit cancellation, async I/O and bounded concurrency. Keep UI code out of engines. Avoid catch-and-ignore, unbounded enumerations and treating paths as stable identities. Dependency versions and SDK patch are pinned in reviewed build configuration when the first build is authorized; do not invent package pins in this documentation-only foundation. Use central package management, locked restore, license review, vulnerability review and an SBOM before distribution. No dependency has yet been selected or downloaded.

## Review and evidence

Use short-lived task branches from main and focused PRs with independent review. Describe changed behavior, acceptance evidence and limits. Owner review covers unresolved architectural choices; review by another AI does not substitute for human release acceptance. Use meaningful unit tests for engine state and integration/fault tests for actual boundaries. Host, filesystem fault, installer, clean-machine, physical USB and release gates remain separate.

Use SemVer once buildable artifacts exist, beginning with unreleased 0.1.0 development candidates. No product version/build is asserted now. Document ownership: requirements own user acceptance; architecture owns interfaces/data/recovery/storage; technology owns evaluation; benchmarks own test recipes; roadmap owns initial tasks; status owns current engineering handoff. Update the owning document instead of duplicating mutable facts.

Release requires accepted V1 evidence, independent review, supported Windows matrix, offline verification, accessibility, dependency/notices/SBOM checks, signed installer policy, upgrade/rollback/retention and recovery verification, and explicit owner release authorization. Do not publish a release merely because tests pass.

## Integrated housekeeping

Use existing Limited Underground working-agreement rules; no separate cleanup scheduler or framework. Before work establish source/ref, output directories, owners and retention holds. During work keep outputs in task-specific ignored artifacts/ or tmp/; redact filesystem names and personal data from public evidence. Preserve manifests, patches, receipts and recovery records. After validation remove only proven disposable current-task output after checking its resolved boundary and active consumers. Never delete by age, discard dirty changes or recursively clean a project/drive root.

Every completion report includes five fields: temporary files removed; temporary files retained and why; worktree status; evidence preserved; cleanup requiring owner approval. Update status before handoff with exact source identity, verification limits, blockers and next authorized action. D: backup location and schedule remain owner decisions: do not create a new backup destination, copy project data there or treat a copy as recovery acceptance. New work and outputs stay in the approved C: workspace or task-specific C: temporary storage.
