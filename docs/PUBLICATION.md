# Proposed GitHub publication

Status: NOT EXECUTED. Explicit owner confirmation is required for these externally visible changes. The final local manifest in ignored artifacts/foundation-review/publication-manifest.json identifies the exact proposed file set and SHA-256 bytes. Review the listed files before approving. No private local evidence is included.

## Identity and access observations — 2026-10-28

The GitHub connector identified an authenticated account but returned no organization memberships. Its lookup of Limited-Underground/File-Organizer returned 404, which cannot distinguish absent repository from unavailable access. Local GitHub CLI API authentication returned HTTP 401. Organization role, repository absence, repository creation permission, Projects write access and existing board identity are therefore unverified. No remote was configured or changed. Do not infer permission from a public organization URL.

Before mutation, establish the authenticated organization role and permissions, recheck repository and exact Project name for existing resources, and inventory any existing contents/protections/items. If either exists, reconcile identity and preserve existing state instead of creating duplicates or overwriting history. Authentication restoration is an owner action if no suitable connected session is available.

## Exact proposed external changes

1. Create public `Limited-Underground/File-Organizer` only if confirmed absent, description “File Organizer by Limited Underground — planning foundation for an offline Windows file-management application.” Default branch `main`; no release, deployment or functional-app claim.
2. Authorize one initial local foundation commit and push of the manifest file set to `refs/heads/main`. Do not include artifacts/, private receipts, workspace portfolio files or user attachments. If the repository already contains history, stop the initial-push path and prepare a focused PR against the actual default branch instead; no force push or automatic merge.
3. Enable issues and private vulnerability reporting; verify the private reporting route. Designate maintainer contacts before community participation. Retain license/NOTICE and required dependency attribution in future distributions.
4. Create/adopt organization Project **File Organizer — Development**, proposed **public** visibility with synthetic technical content only, link it to the repository, and configure Status options exactly Backlog, Ready, In Progress, Validation, Done, Blocked. Add Milestone and dependency links rather than duplicate statuses.
5. Create the ten repository milestones named in ROADMAP.md and eighteen issues FO-001 through FO-018 with the exact outcomes/dependencies/acceptance in its table; link each issue once to the Project. Repository milestones are flat; sequence and issue dependencies express hierarchy. FO-001 enters Validation until setup readback is complete; FO-002 is Blocked pending architecture review; remaining items start Backlog. Owner acceptance records determine later transitions.
6. After initial push, protect `main`: PR required, one independent approval, stale approval dismissal, conversation resolution, no force pushes or deletions, administrator enforcement. Check platform/plan availability and actual independent reviewer availability; do not bypass safeguards. Add required status checks only after real checks exist and run successfully, avoiding a permanently blocked branch. Do not invent a CI test for a documentation-only foundation.
7. Read back repository identity/visibility, exact commit/tree, file hashes, license recognition, private-reporting setting, protection configuration, Project identity/status options, milestone names and issue membership/dependencies. Record any unsupported setting explicitly. This readback verifies publication, not implementation acceptance.

No GitHub change is authorized until the owner approves this proposal. Approval must identify the named repository/main publication, public Project, milestones/issues and safeguards. A changed manifest requires review of changed contents before publication. Architecture/benchmark/implementation approval remains separate.

References: [GitHub protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches), [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/working-with-repository-security-advisories/configuring-private-vulnerability-reporting-for-a-repository).
