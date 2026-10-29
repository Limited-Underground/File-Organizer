# V1 roadmap and validation plan

October 29 execution note: dedicated Project #16 now contains the existing FO-001–FO-018 draft tasks and uses Todo/In Progress/Done. Existing FO-002 owns the owner-authorized first executable architecture checkpoint recorded in PROJECT_STATUS.md. Its bounded local validation does not close broader architecture or later-task acceptance. No duplicate task list was created; the initial milestone/task acceptance below remains engineering sequence.

Status: local proposal for `File Organizer — Development`, 2026-10-28. No issues, milestones or Project items are asserted created by this file. After approved publication, GitHub Projects owns live scope/status/dependencies; this document retains acceptance gates and engineering IDs. Production implementation remains unauthorized until owner architecture acceptance.

## Milestone sequence

GitHub repository milestones are flat, so the numbered sequence expresses hierarchy and dependencies rather than inventing native nested milestones.

| Milestone | Exit gate |
|---|---|
| 1. Project Foundation | Reviewed public foundation published with license, policies, protections and dedicated Project. |
| 2. Requirements and Architecture | Owner accepts requirements, technology choices, storage and recovery contracts; implementation explicitly authorized. |
| 3. Filesystem Inventory and Indexing | Enumeration, identity, persistence, reconciliation and offline-drive requirements pass fixtures and selected Windows qualification. |
| 4. Reliable File Transfer Engine | Durable queue, verification and interruption contracts pass fault-injection scenarios. |
| 5. File Browser and Navigation | Dual panes and transfer/conflict/progress UI pass keyboard and error-state review. |
| 6. Duplicate Detection | Staged hashing, hard-link accounting and protected-backup review pass truth-set fixtures. |
| 7. Storage Analysis and Search | Indexed queries, folder totals, growth and snapshot comparison pass correctness/performance gates. |
| 8. Archive, Quarantine and Recovery | Archive/restore/checkpoint plans pass conflict, interruption and restore verification. |
| 9. Installation and Background Processing | Signed-install strategy, per-user worker lifecycle, upgrades and state retention are qualified. |
| 10. Integration, Testing and V1 Release | Independent review, security/license audit, Windows hardware and clean-install/upgrade acceptance complete; separate owner release authorization. |

## Initial backlog

Create one authoritative issue per ID after publication approval. Use statuses Backlog, Ready, In Progress, Validation, Done and Blocked. Ready means dependencies accepted and execution authorized; Validation means implementation exists but acceptance remains open; Done requires linked evidence and owning requirement signoff. Blocked records a concrete missing decision/dependency. Do not infer execution authority from a board status.

| ID / milestone | Work item | Depends on | Acceptance criteria |
|---|---|---|---|
| FO-001 / 1 | Publish reviewed foundation and configure governance | Owner publication confirmation | Exact reviewed content at approved main ref; license, templates, security channel, Project statuses and branch safeguards verified. |
| FO-002 / 2 | Accept architecture and V1 boundaries | Foundation review | Owner records decisions on UI, worker, installer, transfer metadata, protected backups and performance targets; production authorization explicit. |
| FO-003 / 2 | Implement disposable fixture/fault harness | FO-002 | Deterministic seed and manifest, isolated root guard, bounded resource parameters and repeatable interruption points; no real-user-data access. |
| FO-004 / 3 | Implement filesystem capabilities and identity | FO-003 | NTFS identity, removable fallback, rename/hard-link/reparse and permissions fixtures pass IX-04, RS-02/03. |
| FO-005 / 3 | Implement inventory schema and initial scan | FO-004 | Transactional migrations, generations, scan cancellation/resume and million-record synthetic DB plan validated; IX-01 passes. |
| FO-006 / 3 | Implement incremental tracking and reconciliation | FO-005 | Watcher overflow, USN checkpoint discontinuity and offline remount converge to truth inventory; IX-02/05 pass. |
| FO-007 / 4 | Implement durable operation state machine | FO-003, FO-004, FO-005 | Every side-effect boundary has recoverable journal intent/result; fault replay never silently loses source or overwrites changed target; RS-01 passes. |
| FO-008 / 4 | Implement verified copy/move queue | FO-007 | FM-02/03/04, RS-02 pass pause/resume/cancel, corruption, conflict and cross-volume move cases; unsupported metadata disclosed. |
| FO-009 / 5 | Implement dual-pane desktop navigation | FO-002, FO-004 | FM-01 and keyboard/accessibility/error-state checks pass; UI remains responsive during enumeration. |
| FO-010 / 5 | Integrate transfer/conflict/comparison UI | FO-008, FO-009 | FM-03/04/05 pass including unknown ETA, changing targets, incomplete comparison and partial failures. |
| FO-011 / 6 | Implement staged duplicate detection and review | FO-005, FO-008 | DU-01/02/03 pass known truth corpus, mutation invalidation, hard links and protected backups without destructive defaults. |
| FO-012 / 7 | Implement search, snapshots and storage analysis | FO-006, FO-009 | IX-03/05 and ST-01 pass stable pagination, stale/offline disclosure, hard-link counting and benchmark targets accepted by owner. |
| FO-013 / 8 | Implement archive/quarantine/verified restore | FO-008, FO-011 | ST-02/03 pass unavailable backup, conflicting restore and interrupted restore cases; quarantine has no automatic expiry. |
| FO-014 / 9 | Implement per-user worker lifecycle and IPC | FO-007, FO-009 | Authenticated local IPC, singleton writer, shutdown/restart and session exit tests pass; no implicit service/elevation. |
| FO-015 / 9 | Implement installer and persistent-state migrations | FO-002, FO-005, FO-014 | RS-05 passes clean install, upgrade, failed migration, rollback refusal and retained-state uninstall; signing/distribution reviewed. |
| FO-016 / 10 | Execute integrated reliability and Windows qualification | FO-006, FO-010, FO-011, FO-012, FO-013, FO-015 | Approved matrix passes with evidence separating simulations, actual USB hardware and installer VM tests; unresolved defects triaged. |
| FO-017 / 10 | Independent security, accessibility and license review | FO-016 | Threat model, dependency notices/SBOM, keyboard/screen-reader checks and recovery review accepted; no high-severity unresolved defect. |
| FO-018 / 10 | Owner V1 release acceptance | FO-017 | Requirement traceability complete, known limits and recovery guide accepted, reproducible build/signature verified and release authorization recorded separately. |

## Feasibility evidence

[BENCHMARKS.md](BENCHMARKS.md) owns the reproducible disposable protocols and proposed performance budgets. No benchmark has run; FO-003 implements the harness only after explicit authorization.
