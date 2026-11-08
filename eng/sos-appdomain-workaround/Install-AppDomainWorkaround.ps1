# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

param(
    [Parameter(Mandatory = $true)]
    [string]$OriginalEngineDirectory,
    [Parameter(Mandatory = $true)]
    [string]$DestinationEngineDirectory
)

$ErrorActionPreference = 'Stop'
$original = (Resolve-Path -LiteralPath $OriginalEngineDirectory).Path
$destination = [IO.Path]::GetFullPath($DestinationEngineDirectory)
if (Test-Path -LiteralPath $destination) {
    throw "Destination already exists: $destination"
}
$originalPayload = Join-Path $original 'winext\sos\net462'
if (-not (Test-Path -LiteralPath (Join-Path $originalPayload 'SOS.Extensions.dll'))) {
    throw 'The original engine must contain the complete Framework SOS payload.'
}

Copy-Item -LiteralPath $original -Destination $destination -Recurse
$payload = Join-Path $destination 'winext\sos\net462'
Copy-Item -LiteralPath $originalPayload -Destination (Join-Path $payload 'isolated') -Recurse
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$bootstrap = Join-Path $payload 'SOS.FrameworkBootstrap.dll'
& (Join-Path $framework 'csc.exe') /nologo /noconfig /nostdlib+ /target:library /debug:full /optimize- `
    "/reference:$framework\mscorlib.dll" "/reference:$framework\System.dll" "/reference:$framework\System.Core.dll" `
    "/out:$bootstrap" (Join-Path $PSScriptRoot 'Bootstrap.cs')
if ($LASTEXITCODE -ne 0) {
    throw 'Bootstrap compilation failed.'
}
Move-Item -LiteralPath $bootstrap -Destination (Join-Path $payload 'SOS.Extensions.dll') -Force

$changed = foreach ($file in Get-ChildItem -LiteralPath $original -Recurse -File) {
    $relative = $file.FullName.Substring($original.Length + 1)
    $copy = Join-Path $destination $relative
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
        $relative
    }
}
if (@($changed).Count -ne 1 -or $changed -ne 'winext\sos\net462\SOS.Extensions.dll') {
    throw "Unexpected changes to original package files: $changed"
}
foreach ($file in Get-ChildItem -LiteralPath $originalPayload -Recurse -File) {
    $relative = $file.FullName.Substring($originalPayload.Length + 1)
    $copy = Join-Path (Join-Path $payload 'isolated') $relative
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
        throw "Isolated payload differs from original: $relative"
    }
}
[ordered]@{
    originalEngine = $original
    isolatedEngine = $destination
    changedOriginalFiles = @($changed)
    isolatedPayloadByteIdentical = $true
    bootstrapAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'SOS.Extensions.dll')).FullName
    domainLifetime = 'Process lifetime; native callbacks prevent safe early domain unload.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'appdomain-manifest.json')
Write-Output "APPDOMAIN_ENGINE=$destination"
