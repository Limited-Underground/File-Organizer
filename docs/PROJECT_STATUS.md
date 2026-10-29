# File Organizer development handoff

## Current checkpoint — October 29, 2026

**First executable checkpoint validated locally; broader V1 architecture acceptance remains open.** The owner explicitly authorized this milestone, superseding the foundation-only restriction for this bounded work. No commits, merges or pushes were performed or authorized for the development slice.

Owning record: [FO-002 in Project #16](https://github.com/orgs/Limited-Underground/projects/16?pane=issue&itemId=286303703). Existing roadmap scope and dependencies are retained, with the authorized executable checkpoint appended to that record. No duplicate tasks were created. FO-003 (disposable fixture/fault harness) is the next implementation work package after checkpoint acceptance. Full inventory/browser/IPC work remains pending; none is complete from shell evidence.

Baseline: .NET SDK 10.0.303; Avalonia 12.1.4; Microsoft.Data.Sqlite/Hosting 10.0.14; Application, Persistence, Desktop and one test project. Central pins and package lock files support locked restore. Typed runtime-settings.json controls diagnostic retention. DI composes the store, configuration and worker. The staged in-process host stops with the UI; separate worker/IPC remains FO-014. No placeholder platform adapter, scanning, copy/move/delete, installer, service, release or Linux implementation was added.

Default data uses per-user LocalApplicationData/Limited Underground/File Organizer. SQLite migration 1 creates only SchemaMigration and AppState, with stable profile identity and successful-startup count. It refuses incompatible/corrupt/mismatched schemas without reset. Smoke execution requires a marked disposable ancestor and rejects drive roots, normal profiles, UNC/reparse paths. Shutdown retains the exclusive profile lease until initialization actually completes, including timeout/error paths.

### Verified results and limits

- Complete Debug and Release builds: **zero warnings/errors**, with warnings treated as errors.
- Tests: **19 passed, zero failed/skipped in each configuration**. Cases cover restart persistence, schema/checksum refusal, injected transaction rollback, cancellation, lifecycle/lease ownership, smoke-path isolation, log privacy and retention.
- Actual Windows desktop: two successful launches against one generated profile; schema 1 and startup counts 1 then 2; clean host cancellation, completed worker and released profile lease. Both stderr logs empty. Rendered shell capture visually inspected. This is not accessibility, installer, USB or full V1 acceptance.
- Independent architecture/test/security review corrected unsafe smoke roots, never-started shutdown and timeout lease ownership; seven regression cases were added before successful validation. No material reviewed blockers remain in this checkpoint.

Evidence: ignored artifacts/FO-002-bootstrap-20261029/ contains preimages, initial Git/source context, restore/build logs, Debug/Release TRX, source-manifest.json, binary-manifest.json and dependency-licenses.json. smoke-data/69b40cd16e7c45d28795e40fe472c6c4/ contains two successful receipts, generated SQLite state, empty stderr logs and profile/shell.png. Source hashes identify **uncommitted working files**, not a tested source commit. Failed initial restore/compiler diagnostics remain retained; later successful evidence supersedes them only for its stated gate.

Reproduce with tools/Test-Development.ps1 (locked restore + both build/test configurations), then tools/Test-DesktopSmoke.ps1 -Configuration Release (two native launches and restart checks). SDK/cache/temp locations stay inside the checkout; .NET/Avalonia build telemetry is disabled by the validation runner. For manual navigation review use a task-owned ancestor marker `.file-organizer-smoke-root` containing exactly `File Organizer disposable validation`, then launch the desktop with `--smoke-test --data-root <absolute child profile> --stay-open`. Close normally to exercise cleanup. Do not direct validation at normal user data.

### Source, tracking and backup custody

Published foundation commit `4076939691afb6d9ff789a30659062419d9658d6` was verified through the GitHub connector: all 21 remote files match the approved manifest. Local owner-added tracking instructions are preserved. The CLI still returns HTTP 401 in this execution context; connector access reports repository admin/write and the signed-in browser provides actual Project readback. No credentials or alternative accounts were used.

The approved local root remains on **unborn main**, no configured remote, with foundation and implementation uncommitted; no Git mutation or new worktree was performed. Existing ignored foundation-review evidence/owner-publication checkout is historical material, not another active source root. The artifact/version ledger has no File Organizer entry yet; this task did not alter shared registry code or unrelated product records. The development source is versioned 0.1.0, unreleased, and exact binary hashes are local evidence only.

The existing independently verified D: foundation checkpoint remains unchanged (see workspace Recovery-Records/D-Backup-Execution-20261028/COMPLETION.md). It does **not** include this new source/build evidence; a new separately coordinated checkpoint is required. No D: write or backup-complete claim was made for new work.

Remaining acceptance: owner review of this checkpoint; full worker/IPC behavior, installer authoring/signing/retention qualification, transfer metadata/protected-backup policies, reference hardware/performance budgets and human security/conduct ownership remain their existing gates. No automatic next task or publication follows this checkpoint.

Housekeeping: no temporary files removed. Retain .cache/, src/tests bin/obj and artifacts/FO-002-bootstrap-20261029/ for reproducibility, evidence and review; preserve original artifacts/foundation-review/ and D: custody. No cleanup requiring owner approval is identified.

## Historical foundation checkpoint

The following October 28 snapshot is retained verbatim for provenance. Its planning-only, empty-repository and no-build observations are historical and superseded by the current checkpoint above. Its development-tracking and preservation instructions still apply.

As of 2026-10-28. Authorized outcome: establish the File Organizer planning/development foundation without production functionality. The owner selected this directory as the new project's local root. README and CONTRIBUTING provide the portable reading route; the local workspace portfolio owns membership.

## Current readiness

**READY for owner architecture review. BLOCKED for production implementation.** No application, build, installer, service, release or measured performance exists. No real user-file scan or modification, benchmark execution or software installation was performed.

Local foundation includes requirements, architecture/data/recovery/storage proposal, official-source technology evaluation, disposable benchmark specification, ten milestone gates/eighteen proposed tasks, Apache 2.0 license/notice, contribution/security/conduct policies and GitHub templates. Empty source/test projects were deliberately not created. See [roadmap](ROADMAP.md) for the task list; do not automatically begin it.

The owner established the dedicated public [File Organizer Project #16](https://github.com/orgs/Limited-Underground/projects/16) and an empty public [File-Organizer repository](https://github.com/Limited-Underground/File-Organizer) on October 28. The portfolio backlog audit registered FO-001 through FO-018 as draft tasks. This supersedes the earlier access/setup observations in PUBLICATION.md for resource existence only. No foundation files, commit or push were published; architecture, production implementation and publication acceptance remain separate. FO-001 is not complete merely because the empty resources and task records exist.

## Owner decisions required

1. Accept or revise the requirements and architecture, including Avalonia; proposed Windows 11 24H2 x64 qualification baseline; metadata/alternate-stream behavior on incompatible filesystems; full verification and protected-backup policy. Existing decisions C#/.NET 10/SQLite/free Apache 2.0/offline are retained.
2. Accept per-user worker behavior after UI closure (explicit keep-running or pause/exit), logged-out work excluded from V1, and IPC security boundary.
3. Select MSI authoring tooling after WiX terms review, installer scope/signing/distribution policy, and approve retained-data upgrade/uninstall design.
4. Confirm reference hardware/performance budgets before measured benchmarks; separately authorize expensive, physical USB, VM/installer or system-affecting feasibility tests. Reviewable reproducible recipes exist; no harness or results are claimed.
5. Approve the exact public foundation manifest and GitHub setup proposal, restore/confirm organization access, and designate private security/conduct contacts plus independent reviewers.
6. Explicitly authorize the first bounded production-development task after architecture review. Foundation or publication approval alone does not authorize implementation. Coordinate a D: backup destination separately; no backup was created or claimed.

## Environment and evidence

Observed installed .NET SDKs: 10.0.303 and 8.0.426; Git and GitHub CLI are installed. No SDK/package/IDE installation was attempted. Dependency/package pins await a reviewed first build; official research identifies support and licensing caveats rather than asserting selected package versions.

The local Git repository is initialized on unborn main with no commits or configured remote. Foundation files are uncommitted and preserved. Ignored artifacts/foundation-review/ holds local validation and the publication manifest; these are private task evidence excluded from publication. No additional worktree exists. Reconcile actual Git status and manifest before resuming.

Portfolio membership is registered locally; the existing artifact/version ledger has no File Organizer build because none exists. No fabricated version or percentage is recorded. Next safe action: owner review of this foundation and publication proposal; continue only explicitly approved steps.

## Foundation verification

The local tools/Validate-Foundation.ps1 check passed for 21 proposed publication files: exact untracked-file allowlist, required documents, local Markdown links, private-path/credential-pattern scan, license completeness markers and SHA-256 manifest. The validator's deliberate detection patterns were reviewed separately from its content scan. Independent review clarified cancellation after completed moves and identity-based snapshot rename detection. These are documentation checks only. Apache LICENSE was downloaded from the official unmodified license-text endpoint; SHA-256 is CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30. No build, runtime, filesystem fault, installer or physical acceptance is established.

## Development tracking requirement — October 28, 2026

Before development, identify the authoritative GitHub Issue or draft item on this product's dedicated Project, verify membership and acceptance criteria, and update its status when work genuinely begins. Record the exact source/candidate, implementation evidence, validation results and their limits in that existing task and the owning handoff. Mark Done only after the task's own acceptance criteria pass; keep incomplete, blocked production, target-OS and physical acceptance open. A validated host checkpoint never substitutes for another acceptance layer.

Read back every changed Project item and its evidence reference independently. Report GitHub access or update failures explicitly, including the item and intended update; a local handoff edit does not establish that GitHub tracking is complete. Preserve dependencies, existing evidence and history. Use existing records, with a narrowly scoped missing task only when no appropriate record covers verified completed work. Do not create a second tracking system. If it is not on the task list, do not touch it.
