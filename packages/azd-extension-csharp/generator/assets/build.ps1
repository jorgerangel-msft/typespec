$ErrorActionPreference = 'Stop'
$entryPoint = dotnet msbuild (Join-Path $PSScriptRoot 'Extension.csproj') -getProperty:AssemblyName -nologo
if ($LASTEXITCODE -ne 0) { throw "Reading the extension assembly name failed with exit code $LASTEXITCODE." }
$platforms = if ($env:EXTENSION_PLATFORM) { @($env:EXTENSION_PLATFORM) } else {
    @('windows/amd64', 'windows/arm64', 'linux/amd64', 'linux/arm64', 'darwin/amd64', 'darwin/arm64')
}
$output = if ($env:OUTPUT_DIR) { $env:OUTPUT_DIR } else { Join-Path $PSScriptRoot 'bin' }
$output = [System.IO.Path]::GetFullPath($output)
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($platform in $platforms) {
    $os, $arch = $platform.Split('/')
    $runtimeOS = switch ($os) { 'windows' { 'win' } 'darwin' { 'osx' } 'linux' { 'linux' } default { throw "Unsupported OS: $os" } }
    $runtimeArch = switch ($arch) { 'amd64' { 'x64' } 'arm64' { 'arm64' } default { throw "Unsupported architecture: $arch" } }
    $suffix = if ($os -eq 'windows') { '.exe' } else { '' }
    $publish = Join-Path $PSScriptRoot "obj/publish/$runtimeOS-$runtimeArch"
    dotnet publish (Join-Path $PSScriptRoot 'Extension.csproj') -c Release -r "$runtimeOS-$runtimeArch" --self-contained true -p:PublishSingleFile=true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing $platform failed with exit code $LASTEXITCODE." }
    Copy-Item (Join-Path $publish "$entryPoint$suffix") (Join-Path $output "$entryPoint-$os-$arch$suffix") -Force
}
