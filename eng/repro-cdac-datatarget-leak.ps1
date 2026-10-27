[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CDacDirectory,

    [string]$DumpPath = (
        Join-Path $PSScriptRoot '..\artifacts\tmp\sos-harness\Debug\dumps\core\workstation\heap\net11.0\scenarios\heap.dmp'
    )
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeDirectory = Join-Path $repoRoot 'artifacts\bin\Windows_NT.x64.Debug'
$engineHostDirectory = Join-Path $repoRoot 'artifacts\bin\SOS.TestHarness.EngineHost\Debug\net10.0\win-x64'
$engineHost = Join-Path $engineHostDirectory 'SOS.TestHarness.EngineHost.dll'
$dotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'

foreach ($file in @('mscordaccore_universal.dll', 'mscordbi_universal.dll')) {
    $source = Join-Path $CDacDirectory $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing cDAC component: $source"
    }
    Copy-Item -LiteralPath $source -Destination $nativeDirectory -Force
    Copy-Item -LiteralPath $source -Destination $engineHostDirectory -Force
}

foreach ($required in @($dotnet, $engineHost, $DumpPath, (Join-Path $nativeDirectory 'sos.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required repro input is missing: $required"
    }
}

$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $dotnet
$info.ArgumentList.Add($engineHost)
$info.ArgumentList.Add('dump')
$info.ArgumentList.Add($DumpPath)
$info.UseShellExecute = $false
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.CreateNoWindow = $true
$info.Environment['_NT_SYMBOL_PATH'] = Join-Path $repoRoot 'artifacts\tmp\sos-harness\Debug\symcache'
$info.Environment['SOSHARNESS_HOSTRUNTIME'] = 'None'

$process = [Diagnostics.Process]::Start($info)
$stderrTask = $process.StandardError.ReadToEndAsync()

function Read-Until([string]$marker) {
    while ($true) {
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) {
            throw "EngineHost exited before '$marker'."
        }
        Write-Host $line
        if ($line -eq $marker) {
            return
        }
    }
}

function Send-Command([string]$command) {
    Write-Host "> $command"
    $process.StandardInput.WriteLine($command)
    $process.StandardInput.Flush()
    Read-Until '<<<SOSHARNESS-CMD-END-9F3A>>>'
}

try {
    Read-Until '<<<SOSHARNESS-ENGINE-READY>>>'
    Send-Command '!runtimes --usecdac true'
    Send-Command '!sos eeversion'
    Send-Command '.unload sos'
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        throw 'EngineHost did not exit after SOS unload.'
    }

    $stderr = $stderrTask.GetAwaiter().GetResult()
    Write-Host $stderr
    if ($process.ExitCode -ne 0) {
        throw "EngineHost failed with exit code $($process.ExitCode)."
    }

    $release = [regex]::Match(
        $stderr,
        'SOS_CRT_AFTER_CDAC_RELEASE_REFS=(\d+) LIVE=(\d+) CRT_BLOCK=(\d+) REQUEST=(\d+)')
    $unload = [regex]::Match($stderr, 'SOS_CRT_DATATARGET_LIVE=(\d+)')
    if (-not $release.Success -or -not $unload.Success) {
        throw 'SOS CRT DataTarget markers were not emitted.'
    }

    [pscustomobject]@{
        RemainingComReferences = [int]$release.Groups[1].Value
        LiveAfterRelease = [int]$release.Groups[2].Value
        IsDebugCrtBlock = [int]$release.Groups[3].Value
        AllocationRequest = [long]$release.Groups[4].Value
        LiveAtUnload = [int]$unload.Groups[1].Value
        CrtLeakReported = $stderr.Contains('Detected memory leaks!')
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
    }
    $process.Dispose()
}
