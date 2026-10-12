[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ($Dotnet -eq 'dotnet' -and (Test-Path -LiteralPath '.build/dotnet/dotnet.exe')) {
        $Dotnet = (Resolve-Path -LiteralPath '.build/dotnet/dotnet.exe').Path
    }
    $source = Get-Content -LiteralPath 'Sources/token-bar/main.swift' -Raw
    $version = [regex]::Match($source, '\?\? "(\d+\.\d+\.\d+)"').Groups[1].Value
    if (!$version) { throw 'Could not read the app version from main.swift.' }
    $output = Join-Path $PSScriptRoot "dist/TokenBar-$Runtime"
    # Keep incremental publish output from retaining files from older builds.
    if (Test-Path -LiteralPath $output) {
        if ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Publish directory must not be a link or junction.'
        }
        $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
        $expectedOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "dist/TokenBar-$Runtime"))
        if ($resolvedOutput -ne $expectedOutput) { throw 'Unexpected publish directory.' }
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
    foreach ($project in @('App', 'Cli')) {
        & $Dotnet publish "Windows/TokenBar.$project/TokenBar.$project.csproj" -c $Configuration -r $Runtime `
            --self-contained true -p:PublishSingleFile=true -p:DebugType=None "-p:Version=$version" -o $output --nologo
        if ($LASTEXITCODE -ne 0) { throw "Failed to publish TokenBar.$project" }
    }
    Copy-Item -LiteralPath 'LICENSE' -Destination $output
    Copy-Item -LiteralPath 'Sources/TokenBarCore/Resources/MODELS_DEV_LICENSE' -Destination $output
    Copy-Item -LiteralPath 'Windows/README.md' -Destination (Join-Path $output 'README.md')
    $archive = Join-Path $PSScriptRoot "dist/TokenBar-$version-$Runtime.zip"
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
    Write-Output "Built $archive"
    Write-Output "Run $(Join-Path $output 'TokenBar.exe') to start the tray app."
} finally { Pop-Location }
