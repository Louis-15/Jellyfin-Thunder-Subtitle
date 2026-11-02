#requires -Version 5.1
<#
.SYNOPSIS
    Rewrites manifest.json with the version, checksum and download URL of a release.

.DESCRIPTION
    Jellyfin validates a plugin download against the checksum in the repository manifest,
    so the published zip and the manifest must be written from the same build. The release
    workflow calls this after packaging; it can also be run by hand.

.PARAMETER Version
    The plugin version, e.g. 1.0.0.0.

.PARAMETER Checksum
    MD5 of the release zip (lowercase hex).

.PARAMETER SourceUrl
    Download URL of the release zip.

.PARAMETER Changelog
    Optional changelog line shown in the Jellyfin plugin catalog.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Checksum,
    [Parameter(Mandatory = $true)][string]$SourceUrl,
    [string]$Changelog,
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not populated while parameter defaults are evaluated on PowerShell 5.1.
if (-not $ManifestPath) {
    $ManifestPath = Join-Path (Split-Path -Parent $PSCommandPath) '..\manifest.json'
}

$manifestPath = [System.IO.Path]::GetFullPath($ManifestPath)

# @() keeps a single-entry manifest an array: Jellyfin reads the file as a list of plugins.
$manifest = @([System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json)

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$entry = [pscustomobject]@{
    version   = $Version
    changelog = if ($Changelog) { $Changelog } else { "Release $Version" }
    targetAbi = $manifest[0].versions[0].targetAbi
    sourceUrl = $SourceUrl
    checksum  = $Checksum.ToLowerInvariant()
    timestamp = $timestamp
}

# Keep older versions listed so someone on an older server build can still install them.
$versions = @($entry) + @($manifest[0].versions | Where-Object { $_.version -ne $Version })
$manifest[0].versions = $versions

$json = ConvertTo-Json -InputObject $manifest -Depth 8
[System.IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "manifest.json -> $Version ($($entry.checksum))"
