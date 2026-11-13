param(
    [string]$PackageVersion,
    [string]$ArtifactDirectory
)

$ErrorActionPreference = 'Stop'

$softRoot = $PSScriptRoot
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $softRoot)
$projectPath = Join-Path $softRoot 'TrueNorth.TradeFlow.csproj'
$setupScript = Join-Path $softRoot 'installer\setup.iss'
$projectVersion = ([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.Version
$version = if ([string]::IsNullOrWhiteSpace($PackageVersion)) { $projectVersion } else { $PackageVersion }

if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "PackageVersion must be a three-part numeric version: $version"
}

$artifactRoot = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    Join-Path $workspaceRoot 'artifacts\001-windows-shell'
} elseif ([System.IO.Path]::IsPathRooted($ArtifactDirectory)) {
    [System.IO.Path]::GetFullPath($ArtifactDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $ArtifactDirectory))
}
$workspacePrefix = [System.IO.Path]::GetFullPath($workspaceRoot).TrimEnd('\') + '\'
if (-not $artifactRoot.StartsWith($workspacePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ArtifactDirectory must stay inside the workspace: $artifactRoot"
}

$artifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
$setupBaseName = "TrueNorth-TradeFlow-Setup-$version-win-x64"
$setupName = "$setupBaseName.exe"
$finalSetupPath = Join-Path $artifactRoot $setupName
$compileRoot = Join-Path $artifactRoot ('.compile-' + [Guid]::NewGuid().ToString('N'))
$publishDir = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    Join-Path $compileRoot 'publish'
} else {
    Join-Path $artifactRoot 'publish'
}

if (-not [string]::IsNullOrWhiteSpace($ArtifactDirectory) -and (Test-Path -LiteralPath $publishDir)) {
    throw "ArtifactDirectory must use a fresh publish directory: $publishDir"
}
if (-not [string]::IsNullOrWhiteSpace($ArtifactDirectory) -and (Test-Path -LiteralPath $finalSetupPath)) {
    throw "ArtifactDirectory already contains the expected package: $finalSetupPath"
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
New-Item -ItemType Directory -Force -Path $compileRoot | Out-Null
Push-Location $softRoot
try {
    $assemblyVersion = "$version.0"
    dotnet publish .\TrueNorth.TradeFlow.csproj -c Release -r win-x64 --self-contained true `
        -p:Version=$version -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$assemblyVersion `
        -p:PublishSingleFile=false -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$runtimeConfigPath = Join-Path $publishDir 'TrueNorth.TradeFlow.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $runtimeConfigPath)) {
    throw "The self-contained runtime configuration is missing: $runtimeConfigPath"
}
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
if ($frameworks.Count -eq 0) {
    $frameworks = @($runtimeConfig.runtimeOptions.frameworks)
}
foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    if (-not ($frameworks | Where-Object { $_.name -eq $framework -and $_.version -eq '10.0.14' })) {
        throw "The publish is missing the required $framework 10.0.14 runtime."
    }
}
foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $runtimeFile))) {
        throw "The self-contained .NET runtime payload is incomplete: $runtimeFile"
    }
}
foreach ($playwrightFile in @('playwright.ps1', '.playwright\package\cli.js', '.playwright\node\win32_x64\node.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $playwrightFile))) {
        throw "The packaged Playwright payload is missing: $playwrightFile"
    }
}

$publishedVersion = (Get-Item -LiteralPath (Join-Path $publishDir 'TrueNorth.TradeFlow.exe')).VersionInfo.FileVersion
if ($publishedVersion -notlike "$version.*") {
    throw "The published application version '$publishedVersion' does not match package version '$version'."
}

$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:USERPROFILE 'AppData\Local\Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:USERPROFILE 'AppData\Local\Programs\Inno Setup 7\ISCC.exe')
)
$compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compilerPath -and $compilerCommand) {
    $compilerPath = $compilerCommand.Source
}
if (-not $compilerPath) {
    throw 'No user-local Inno Setup compiler was found. Install one from jrsoftware.org, then rerun this script.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $compilerPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') {
    throw "The Inno Setup compiler signature or publisher is not verified: $compilerPath"
}

$publishRelativePath = [System.IO.Path]::GetRelativePath((Split-Path -Parent $setupScript), $publishDir)
$publishDefine = '/DPublishDir=' + $publishRelativePath
& $compilerPath "/DAppVersion=$version" $publishDefine "/O$compileRoot" "/F$setupBaseName" $setupScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$compiledSetupPath = Join-Path $compileRoot $setupName
if (-not (Test-Path -LiteralPath $compiledSetupPath)) {
    throw "Expected installer was not created: $compiledSetupPath"
}

Move-Item -LiteralPath $compiledSetupPath -Destination $finalSetupPath -Force
Get-FileHash -LiteralPath $finalSetupPath -Algorithm SHA256
Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object | Select-Object Count

if ((Test-Path -LiteralPath $compileRoot) -and [System.IO.Path]::GetFullPath($compileRoot).StartsWith($workspacePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $compileRoot -Recurse -Force
}
