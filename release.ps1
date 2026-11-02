#requires -Version 5.1
<#
.SYNOPSIS
    Cuts a new release: bumps the version everywhere, commits, tags and pushes.

.DESCRIPTION
    The tag push starts the GitHub workflow, which tests, packages, writes the checksum
    into manifest.json, commits it back and publishes the release asset. That keeps the
    zip Jellyfin downloads and the checksum in the manifest identical.

.EXAMPLE
    .\release.ps1 -Version 1.0.1.0 -Changelog "修复 xxx"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Changelog
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "版本号格式应为 1.2.3 或 1.2.3.4，收到：$Version"
}

Write-Host "==> 更新版本号到 $Version" -ForegroundColor Cyan

$utf8 = New-Object System.Text.UTF8Encoding($false)

# csproj
$csproj = Join-Path $root 'Jellyfin.Plugin.ThunderSubtitle\Jellyfin.Plugin.ThunderSubtitle.csproj'
$text = [System.IO.File]::ReadAllText($csproj)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
$text = [regex]::Replace($text, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version</AssemblyVersion>")
$text = [regex]::Replace($text, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version</FileVersion>")
[System.IO.File]::WriteAllText($csproj, $text, $utf8)

# meta.json
$metaPath = Join-Path $root 'meta.json'
$meta = [System.IO.File]::ReadAllText($metaPath) | ConvertFrom-Json
$meta.version = $Version
[System.IO.File]::WriteAllText($metaPath, ($meta | ConvertTo-Json -Depth 5) + "`n", $utf8)

# manifest.json: keep the entry list, only move the version so the workflow can fill
# in the checksum of the artefact it builds.
$manifestPath = Join-Path $root 'manifest.json'
$manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$existing = $manifest[0].versions | Where-Object { $_.version -eq $Version }
if (-not $existing) {
    $manifest[0].versions = @([pscustomobject]@{
        version   = $Version
        changelog = if ($Changelog) { $Changelog } else { "Release $Version" }
        targetAbi = $manifest[0].versions[0].targetAbi
        sourceUrl = "https://github.com/Louis-15/Jellyfin-Thunder-Subtitle/releases/download/v$Version/thunder-subtitle_$Version.zip"
        checksum  = ''
        timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }) + @($manifest[0].versions)
}
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8) + "`n", $utf8)

Write-Host "==> 提交并打标签 v$Version" -ForegroundColor Cyan
git -C $root add -A
git -C $root commit -m "Release $Version" -m $(if ($Changelog) { $Changelog } else { "Release $Version" })
if ($LASTEXITCODE -ne 0) { throw 'git commit 失败' }

git -C $root tag "v$Version"
git -C $root push origin main
git -C $root push origin "v$Version"

Write-Host ''
Write-Host "已推送 v$Version。GitHub Actions 会完成编译、打包、写入 checksum 并发布 Release：" -ForegroundColor Green
Write-Host "  https://github.com/Louis-15/Jellyfin-Thunder-Subtitle/actions"
Write-Host "  https://github.com/Louis-15/Jellyfin-Thunder-Subtitle/releases"
