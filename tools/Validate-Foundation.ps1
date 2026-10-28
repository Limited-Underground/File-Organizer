param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$paths = @(
  '.editorconfig', '.gitattributes', '.gitignore',
  '.github/ISSUE_TEMPLATE/bug_report.md', '.github/ISSUE_TEMPLATE/task.md',
  '.github/pull_request_template.md', 'AGENTS.md', 'CODE_OF_CONDUCT.md',
  'CONTRIBUTING.md', 'LICENSE', 'NOTICE', 'README.md', 'SECURITY.md',
  'docs/ARCHITECTURE.md', 'docs/BENCHMARKS.md', 'docs/PROJECT_STATUS.md',
  'docs/PUBLICATION.md', 'docs/REQUIREMENTS.md', 'docs/ROADMAP.md',
  'docs/TECHNOLOGY.md', 'tools/Validate-Foundation.ps1'
)
$receiptDir = Join-Path $projectRoot 'artifacts/foundation-review'
[IO.Directory]::CreateDirectory($receiptDir) | Out-Null
$records = foreach ($relative in $paths) {
  $absolute = Join-Path $projectRoot $relative
  if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Missing: $relative" }
  $contents = [IO.File]::ReadAllText($absolute)
  if ($relative -ne 'tools/Validate-Foundation.ps1' -and $contents -match '(?i)C:\\Users\\|C:\\Projects\\|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]+|sk-proj-') {
    throw "Private path or credential pattern: $relative"
  }
  if ($relative.EndsWith('.md')) {
    foreach ($match in [regex]::Matches($contents, '\[[^\]]*\]\(([^)]+)\)')) {
      $target = $match.Groups[1].Value
      if ($target -match '^(https?://|#|mailto:)') { continue }
      $localTarget = ($target -split '#')[0]
      if ($localTarget -and -not (Test-Path -LiteralPath (Join-Path (Split-Path $absolute) $localTarget))) {
        throw "Broken local link in ${relative}: $target"
      }
    }
  }
  [ordered]@{ path = $relative; bytes = (Get-Item -LiteralPath $absolute).Length; sha256 = (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash }
}
$untracked = @(& git -C $projectRoot ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Git inventory failed' }
$unexpected = @($untracked | Where-Object { $_ -notin $paths })
if ($unexpected.Count) { throw "Unexpected publishable files: $($unexpected -join ', ')" }
$licenseText = [IO.File]::ReadAllText((Join-Path $projectRoot 'LICENSE'))
if ($licenseText -notmatch 'Version 2.0, January 2004' -or $licenseText -notmatch 'END OF TERMS AND CONDITIONS') { throw 'License incomplete' }
[ordered]@{ generatedUtc = [DateTime]::UtcNow.ToString('o'); destination = 'Limited-Underground/File-Organizer'; ref = 'refs/heads/main'; files = @($records) } |
  ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $receiptDir 'publication-manifest.json') -Encoding utf8
[ordered]@{ checkedUtc = [DateTime]::UtcNow.ToString('o'); outcome = 'PASS'; fileCount = $records.Count; checks = @('exact untracked file allowlist', 'required files', 'local Markdown links', 'private-path and credential-pattern scan', 'Apache license markers', 'SHA-256 manifest'); limits = 'Documentation validation only; no build, benchmark, runtime, installer or external setup verified.' } |
  ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $receiptDir 'validation.json') -Encoding utf8
Write-Output "PASS: $($records.Count) foundation files; local links, pattern scan and manifest. No application validation claimed."
