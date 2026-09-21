# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^1\.15\.0-jsonpath\.local\.[A-Za-z0-9.-]+$')]
    [string] $PackageVersion,
    [Parameter(Mandatory)]
    [string] $PackageFeed,
    [ValidateSet('restore', 'build', 'test')]
    [string] $Action = 'test',
    [string] $Project = 'Microsoft.TypeSpec.Generator.ClientModel\test\Microsoft.TypeSpec.Generator.ClientModel.Tests.csproj',
    [string[]] $DotNetArguments = @()
)

$ErrorActionPreference = 'Stop'
$generator = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$feed = (Resolve-Path -LiteralPath $PackageFeed).Path
$projectPath = Join-Path $generator $Project
# Keep the same depth as artifacts: existing test helpers locate fixtures by walking five parents.
$artifacts = Join-Path $generator 'temp\'
$properties = @(
    "-p:ComposesJsonPathPackageVersion=$PackageVersion",
    "-p:ComposesJsonPathPackageFeed=$feed",
    "-p:ArtifactsDir=$artifacts"
)

Push-Location $generator
try {
    & dotnet $Action $projectPath @properties @DotNetArguments
    $result = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $result
