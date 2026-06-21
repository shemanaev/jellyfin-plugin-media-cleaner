param(
    [string]$Version,
    [switch]$NoGenerate
)

$ErrorActionPreference = "Stop"

$ProfilesPath = Join-Path $PSScriptRoot "jellyfin-profiles.json"
$GenerateProfilesScriptPath = Join-Path $PSScriptRoot "generate-jellyfin-props.ps1"

$profilesConfig = Get-Content -Raw -Path $ProfilesPath | ConvertFrom-Json
$oldVersion = [string]$profilesConfig.baseVersion

if ([string]::IsNullOrWhiteSpace($Version)) {
    $oldVersionParts = $oldVersion -split '\.'
    if ($oldVersionParts.Count -ne 3) {
        throw "Current baseVersion must use major.minor.patch format to auto-bump minor version."
    }

    $major = [int]$oldVersionParts[0]
    $minor = [int]$oldVersionParts[1]
    $Version = "$major.$($minor + 1).0"
} elseif ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use major.minor.patch format, for example 2.25.0."
}

if ($oldVersion -eq $Version) {
    Write-Host "Plugin base version is already $Version."
} else {
    $profilesConfig.baseVersion = $Version
    $json = ($profilesConfig | ConvertTo-Json -Depth 10) -replace "\r\n?", "`n"
    [System.IO.File]::WriteAllText(
        $ProfilesPath,
        ($json + "`n"),
        [System.Text.Encoding]::ASCII)

    Write-Host "Plugin base version bumped: $oldVersion -> $Version"
}

if (-not $NoGenerate) {
    & $GenerateProfilesScriptPath
}

Write-Host "Jellyfin package versions:"
foreach ($buildProfile in @($profilesConfig.buildProfiles)) {
    Write-Host "  $($buildProfile.profile): $Version.$($buildProfile.versionPatchOffset)"
}
