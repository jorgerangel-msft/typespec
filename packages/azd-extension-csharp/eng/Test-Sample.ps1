#Requires -Version 7.3

param(
    [string] $ConfigDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version 3.0

$packageRoot = Split-Path $PSScriptRoot -Parent
if (-not $ConfigDirectory) {
    $ConfigDirectory = Join-Path $packageRoot 'test' 'output' 'manual-azd'
}
$ConfigDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigDirectory)
if ($IsWindows -and $ConfigDirectory.Contains(' ')) {
    throw 'azd 1.37.2 cannot launch the developer extension from a configuration path containing spaces. Use -ConfigDirectory with a dedicated path without spaces.'
}
Get-Command npm, dotnet, azd -ErrorAction Stop | Out-Null

$extension = Join-Path $packageRoot 'samples' 'tagger' 'tsp-output' '@typespec' 'azd-extension-csharp' 'src' 'Generated'
$bundles = Join-Path $ConfigDirectory 'bundles'
$previousEnvironment = @{
    AZD_CONFIG_DIR = $env:AZD_CONFIG_DIR
    AZD_NON_INTERACTIVE = $env:AZD_NON_INTERACTIVE
    AZURE_DEV_COLLECT_TELEMETRY = $env:AZURE_DEV_COLLECT_TELEMETRY
}

Push-Location $packageRoot
try {
    $env:AZD_CONFIG_DIR = $ConfigDirectory
    $env:AZD_NON_INTERACTIVE = 'true'
    $env:AZURE_DEV_COLLECT_TELEMETRY = 'no'

    npm run build
    npm run generate:sample

    azd extension install microsoft.azd.extensions --no-prompt
    azd x build --cwd $extension --skip-install --no-prompt
    azd x pack --bundle --cwd $extension --output $bundles --no-prompt

    $manifest = Get-Content (Join-Path $extension 'extension.yaml') -Raw | ConvertFrom-Json
    $bundle = Join-Path $bundles "$($manifest.id.Replace('.', '-'))_$($manifest.version).zip"
    azd extension install $bundle --force --no-prompt

    azd tagger show
    azd tagger show --help
    azd tagger show --output json

    Write-Host 'To use this isolated installation after the script:'
    Write-Host "`$env:AZD_CONFIG_DIR = '$($ConfigDirectory.Replace("'", "''"))'"
    Write-Host 'azd tagger show'
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
