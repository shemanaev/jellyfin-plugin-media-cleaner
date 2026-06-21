param(
    [ValidateSet("BuildProfiles", "Profiles", "GitHubBuildMatrix", "GitHubMatrix", "ServerProfiles", "SmokeMatrix")]
    [string]$Format = "BuildProfiles"
)

$ErrorActionPreference = "Stop"

$ProfilesPath = Join-Path $PSScriptRoot "jellyfin-profiles.json"
$profilesConfig = Get-Content -Raw -Path $ProfilesPath | ConvertFrom-Json
$buildProfiles = @($profilesConfig.buildProfiles)
$serverProfiles = @($profilesConfig.serverProfiles)

function Get-BuildProfile {
    param([string]$Profile)

    $serverProfile = $serverProfiles | Where-Object { $_.profile -eq $Profile } | Select-Object -First 1
    if ($serverProfile) {
        return [string]$serverProfile.buildProfile
    }

    $buildProfile = $buildProfiles | Where-Object { $_.profile -eq $Profile } | Select-Object -First 1
    if ($buildProfile) {
        return [string]$buildProfile.profile
    }

    throw "Unknown Jellyfin profile '$Profile'"
}

function Get-BuildProfileMetadata {
    param([string]$Profile)

    $resolvedBuildProfile = Get-BuildProfile -Profile $Profile
    return $buildProfiles | Where-Object { $_.profile -eq $resolvedBuildProfile } | Select-Object -First 1
}

switch ($Format) {
    { $_ -in @("BuildProfiles", "Profiles") } {
        $buildProfiles | ForEach-Object { $_.profile }
    }
    { $_ -in @("GitHubBuildMatrix", "GitHubMatrix") } {
        $matrix = [ordered]@{
            include = @(
                $buildProfiles | ForEach-Object {
                    [ordered]@{
                        "jellyfin-profile" = $_.profile
                        "jellyfin-build-profile" = $_.profile
                        "dotnet-target" = $_.targetFramework
                    }
                }
            )
        }

        $matrix | ConvertTo-Json -Compress -Depth 5
    }
    "ServerProfiles" {
        $serverProfiles | ForEach-Object { $_.profile }
    }
    "SmokeMatrix" {
        $matrix = [ordered]@{
            include = @(
                $serverProfiles | ForEach-Object {
                    $buildProfile = Get-BuildProfileMetadata -Profile $_.profile
                    [ordered]@{
                        "jellyfin-profile" = $_.profile
                        "jellyfin-build-profile" = $buildProfile.profile
                        "dotnet-target" = $buildProfile.targetFramework
                    }
                }
            )
        }

        $matrix | ConvertTo-Json -Compress -Depth 5
    }
}
