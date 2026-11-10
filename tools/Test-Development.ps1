#requires -Version 7.2
param([switch]$SkipRestore, [ValidateSet('FO-003-fixtures-20261029','FO-005-indexing-20261029','FO-005-recovery-20261029','FO-005-streaming-20261029','FO-012-indexed-ui-20261029','FO-012-search-20261029','FO-012-filters-20261101')][string]$EvidenceTask = 'FO-012-filters-20261101')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $evidence = Join-Path $projectRoot ('artifacts/' + $EvidenceTask)
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.cache/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.cache/nuget-http'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $projectRoot '.cache/nuget-plugins'
    $env:TEMP = Join-Path $projectRoot '.cache/temp'
    $env:TMP = $env:TEMP
    $env:NUGET_SCRATCH = Join-Path $env:TEMP 'NuGetScratch'
    $env:FILE_ORGANIZER_TEST_ROOT = $projectRoot
    $env:FILE_ORGANIZER_EVIDENCE_TASK = $EvidenceTask
    [IO.Directory]::CreateDirectory($env:TEMP) | Out-Null
    function Invoke-DotNet([string]$Log, [string[]]$Arguments) {
        & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence $Log)
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Log (exit $LASTEXITCODE)" }
    }
    if (-not $SkipRestore) {
        Invoke-DotNet 'restore-locked.log' @('restore','FileOrganizer.slnx','--locked-mode','--configfile','NuGet.Config','--disable-parallel','-m:1','-nr:false','--verbosity','minimal')
    }
    foreach ($configuration in @('Debug','Release')) {
        Invoke-DotNet "build-$configuration.log" @('build','FileOrganizer.slnx','--no-restore','--configuration',$configuration,'-m:1','-nr:false','-p:UseSharedCompilation=false','--verbosity','minimal')
        Invoke-DotNet "test-$configuration.log" @('test','tests/FileOrganizer.Tests/FileOrganizer.Tests.csproj','--no-build','--no-restore','--configuration',$configuration,'--logger',"trx;LogFileName=$configuration.trx",'--results-directory',(Join-Path $evidence 'test-results'))
    }
    Write-Output "Build/test evidence retained in $evidence. Actual desktop smoke validation is separate."
}
finally { Pop-Location }