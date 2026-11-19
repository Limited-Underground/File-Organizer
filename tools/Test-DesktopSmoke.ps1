#requires -Version 7.2
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = Join-Path $projectRoot 'artifacts/FO-002-bootstrap-20261029'
$fixture = Join-Path $evidence ('smoke-data/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture '.file-organizer-smoke-root'), 'File Organizer disposable validation')
$profile = Join-Path $fixture 'profile'
$executable = Join-Path $projectRoot "src/FileOrganizer.Desktop/bin/$Configuration/net10.0/FileOrganizer.Desktop.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the selected desktop configuration first.' }
$results = @()
for ($iteration = 1; $iteration -le 2; $iteration++) {
    $process = Start-Process -FilePath $executable -ArgumentList @('--smoke-test','--data-root',('"' + $profile + '"')) -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $fixture "stderr-$iteration.log")
    if (-not $process.WaitForExit(60000)) { throw "Smoke run $iteration timed out; process $($process.Id) retained for investigation." }
    if ($process.ExitCode -ne 0) { throw "Smoke run $iteration failed (exit $($process.ExitCode)); preserve $fixture" }
    $receipt = Get-Content -LiteralPath (Join-Path $profile 'smoke-result.json') -Raw | ConvertFrom-Json
    if (-not $receipt.success -or -not $receipt.cleanShutdown -or -not $receipt.workerCompleted -or $receipt.startupCount -ne $iteration -or $receipt.schemaVersion -ne 1) { throw "Smoke receipt $iteration failed" }
    $lock = [IO.File]::Open((Join-Path $profile 'profile.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $lock.Dispose()
    Copy-Item -LiteralPath (Join-Path $profile 'smoke-result.json') -Destination (Join-Path $fixture "receipt-$iteration.json")
    $results += $receipt
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'restart-results.json') -Encoding utf8
Write-Output "PASS: two actual Windows desktop launches, clean cancellation/shutdown, released lease and SQLite restart count 1 -> 2. Evidence: $fixture"
