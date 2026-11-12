#requires -Version 5.1
<#
.SYNOPSIS
    Builds the plugin and packs the Jellyfin plugin zip.

.DESCRIPTION
    Produces artifacts/Jellyfin.Plugin.ThunderSubtitle_<version>/ (dll + meta.json + logo.png)
    and artifacts/thunder-subtitle_<version>.zip, which is what
    /config/plugins/<name>_<version>/ expects after extraction.

.PARAMETER Configuration
    Build configuration, Release by default.

.PARAMETER Dotnet
    Path to the dotnet executable. Defaults to "dotnet" on PATH, then %DOTNET_ROOT%\dotnet.exe.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Dotnet) {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $Dotnet = 'dotnet'
    } elseif ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $Dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    } else {
        throw '找不到 dotnet。请安装 .NET 10 SDK，或用 -Dotnet 指定路径。'
    }
}

$project = Join-Path $root 'Jellyfin.Plugin.ThunderSubtitle\Jellyfin.Plugin.ThunderSubtitle.csproj'
# Read as UTF-8 explicitly: Windows PowerShell 5.1 would otherwise use the ANSI code page.
$meta = [System.IO.File]::ReadAllText((Join-Path $root 'meta.json')) | ConvertFrom-Json
$version = $meta.version
$artifacts = Join-Path $root 'artifacts'
$packageDir = Join-Path $artifacts "Jellyfin.Plugin.ThunderSubtitle_$version"
$zipPath = Join-Path $artifacts "thunder-subtitle_$version.zip"

Write-Host "==> 编译 $version ($Configuration)" -ForegroundColor Cyan
& $Dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "编译失败（exit $LASTEXITCODE）"
}

$buildOutput = Join-Path $root "Jellyfin.Plugin.ThunderSubtitle\bin\$Configuration\net10.0"

if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

Copy-Item (Join-Path $buildOutput 'Jellyfin.Plugin.ThunderSubtitle.dll') $packageDir
Copy-Item (Join-Path $root 'meta.json') $packageDir
Copy-Item (Join-Path $root 'logo.png') $packageDir

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
Write-Host ''
Write-Host "插件目录: $packageDir"
Write-Host "插件压缩包: $zipPath"
Write-Host "MD5: $hash"
Write-Host ''
Write-Host '安装到 Jellyfin（NAS / Docker 同样适用）：'
Write-Host '  方式 1：Jellyfin 控制台 →「插件 → 仓库」添加 manifest 地址，再在「目录」里安装（可自动更新）'
Write-Host "  方式 2：把 $(Split-Path $zipPath -Leaf) 解压到 Jellyfin 配置目录下的 plugins/ThunderSubtitle_$version/，再重启 Jellyfin"
Write-Host '  Docker / NAS：目标路径是容器内的 /config/plugins，宿主机对应你映射给容器的配置目录'
