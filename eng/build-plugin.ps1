param(
    [string[]]$Profile,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot "MediaCleaner/MediaCleaner.csproj"
$BuildYamlScriptPath = Join-Path $PSScriptRoot "write-build-yaml.ps1"
$ProfilesScriptPath = Join-Path $PSScriptRoot "get-jellyfin-profiles.ps1"
$ProfilesPath = Join-Path $PSScriptRoot "jellyfin-profiles.json"
$ArtifactsRoot = Join-Path $RepoRoot "artifacts"

if ($null -eq $Profile -or $Profile.Count -eq 0) {
    $Profile = @(& $ProfilesScriptPath -Format BuildProfiles)
    if ($LASTEXITCODE -ne 0 -or $Profile.Count -eq 0) {
        throw "$ProfilesScriptPath failed to return Jellyfin profiles"
    }
}

$profilesConfig = Get-Content -Raw -Path $ProfilesPath | ConvertFrom-Json
$buildProfiles = @($profilesConfig.buildProfiles)
$serverProfiles = @($profilesConfig.serverProfiles)

function Resolve-BuildProfile {
    param([string]$CurrentProfile)

    $serverProfile = $serverProfiles | Where-Object { $_.profile -eq $CurrentProfile } | Select-Object -First 1
    if ($serverProfile) {
        return [string]$serverProfile.buildProfile
    }

    $buildProfile = $buildProfiles | Where-Object { $_.profile -eq $CurrentProfile } | Select-Object -First 1
    if ($buildProfile) {
        return [string]$buildProfile.profile
    }

    throw "Unknown Jellyfin profile '$CurrentProfile'"
}

$Profile = @(
    $Profile |
        ForEach-Object { Resolve-BuildProfile -CurrentProfile $_ } |
        Select-Object -Unique
)

function Invoke-DotNet {
    param(
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

foreach ($currentProfile in $Profile) {
    $outputPath = Join-Path $ArtifactsRoot $currentProfile

    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

    Invoke-DotNet -Arguments @(
        "build",
        $ProjectPath,
        "-c",
        $Configuration,
        "-p:JellyfinProfile=$currentProfile",
        "-p:OutputPath=$outputPath/"
    )

    & $BuildYamlScriptPath -Profile $currentProfile -OutputPath (Join-Path $outputPath "build.yaml")
    if ($LASTEXITCODE -ne 0) {
        throw "$BuildYamlScriptPath failed with exit code $LASTEXITCODE"
    }
}
