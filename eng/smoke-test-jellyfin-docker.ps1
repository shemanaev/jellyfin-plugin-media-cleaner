param(
    [string[]]$Profile,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [int]$TimeoutSeconds = 90,
    [string]$DockerPath,
    [switch]$SkipBuild,
    [switch]$KeepContainers
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ArtifactsRoot = Join-Path $RepoRoot "artifacts"
$SmokeRoot = Join-Path $ArtifactsRoot "smoke"
$BuildScript = Join-Path $PSScriptRoot "build-plugin.ps1"
$ProfilesScriptPath = Join-Path $PSScriptRoot "get-jellyfin-profiles.ps1"
$ProfilesPath = Join-Path $PSScriptRoot "jellyfin-profiles.json"
$ProjectPath = Join-Path $RepoRoot "MediaCleaner/MediaCleaner.csproj"

if ($null -eq $Profile -or $Profile.Count -eq 0) {
    $Profile = @(& $ProfilesScriptPath -Format ServerProfiles)
    if ($LASTEXITCODE -ne 0 -or $Profile.Count -eq 0) {
        throw "$ProfilesScriptPath failed to return Jellyfin profiles"
    }
}

$profilesConfig = Get-Content -Raw -Path $ProfilesPath | ConvertFrom-Json
$buildProfiles = @($profilesConfig.buildProfiles)
$serverProfiles = @($profilesConfig.serverProfiles)
$pluginVersionsByBuildProfile = @{}
$pluginArtifactsByBuildProfile = @{}

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

function Get-PluginVersion {
    param([string]$BuildProfile)

    if (-not $pluginVersionsByBuildProfile.ContainsKey($BuildProfile)) {
        $pluginVersion = (& dotnet msbuild $ProjectPath -nologo -p:JellyfinProfile=$BuildProfile -getProperty:PluginVersion).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pluginVersion)) {
            throw "Unable to resolve PluginVersion for Jellyfin build profile '$BuildProfile'."
        }

        $pluginVersionsByBuildProfile[$BuildProfile] = $pluginVersion
    }

    return $pluginVersionsByBuildProfile[$BuildProfile]
}

function Get-PluginArtifacts {
    param([string]$BuildProfile)

    if (-not $pluginArtifactsByBuildProfile.ContainsKey($BuildProfile)) {
        $pluginArtifacts = (& dotnet msbuild $ProjectPath -nologo -p:JellyfinProfile=$BuildProfile -getProperty:PluginArtifacts).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pluginArtifacts)) {
            throw "Unable to resolve PluginArtifacts for Jellyfin build profile '$BuildProfile'."
        }

        $pluginArtifactsByBuildProfile[$BuildProfile] = @(
            $pluginArtifacts -split ";" |
                ForEach-Object { $_.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
    }

    return $pluginArtifactsByBuildProfile[$BuildProfile]
}

if ([string]::IsNullOrWhiteSpace($DockerPath)) {
    $dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
    if ($dockerCommand) {
        $DockerPath = $dockerCommand.Source
    } else {
        $candidateDockerPaths = @(
            (Join-Path $env:ProgramFiles "Docker/Docker/resources/bin/docker.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs/DockerDesktop/resources/bin/docker.exe")
        )
        $DockerPath = $candidateDockerPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($DockerPath) -or -not (Test-Path $DockerPath)) {
    throw "docker.exe was not found. Add Docker to PATH or pass -DockerPath <path-to-docker.exe>."
}

$dockerBinPath = Split-Path -Parent $DockerPath
if (($env:PATH -split [System.IO.Path]::PathSeparator) -notcontains $dockerBinPath) {
    $env:PATH = "$dockerBinPath$([System.IO.Path]::PathSeparator)$env:PATH"
}

function Invoke-External {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Remove-ContainerIfExists {
    param([string]$Name)

    $existing = & $DockerPath ps -a --filter "name=^/$Name$" --format "{{.Names}}"
    if ($LASTEXITCODE -ne 0) {
        throw "docker ps failed with exit code $LASTEXITCODE"
    }

    if ($existing -contains $Name) {
        & $DockerPath rm -f $Name | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "docker rm -f $Name failed with exit code $LASTEXITCODE"
        }
    }
}

function Wait-JellyfinStartup {
    param(
        [string]$ContainerName,
        [int]$Timeout
    )

    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        Start-Sleep -Seconds 2

        $state = (& $DockerPath inspect -f "{{.State.Status}}" $ContainerName 2>$null)
        if ($LASTEXITCODE -ne 0 -or $state -eq "exited" -or $state -eq "dead") {
            return $false
        }

        $logs = (& $DockerPath logs $ContainerName 2>&1) -join "`n"
        if ($logs -match "Startup complete|Now listening on|Application started") {
            return $true
        }
    } while ((Get-Date) -lt $deadline)

    return $false
}

function Test-PluginLogs {
    param(
        [string]$ContainerName,
        [string]$CurrentProfile
    )

    $logs = (& $DockerPath logs $ContainerName 2>&1) -join "`n"
    $failurePattern = @(
        "PluginLoadException",
        "MissingMethodException",
        "MissingFieldException",
        "TypeLoadException",
        "FileLoadException",
        "FileNotFoundException",
        "Could not load file or assembly",
        "Failed to load assembly",
        "Error loading plugin",
        "Failed.*MediaCleaner",
        "MediaCleaner.*Failed",
        "Error.*MediaCleaner",
        "MediaCleaner.*Error",
        "Exception.*MediaCleaner",
        "MediaCleaner.*Exception"
    ) -join "|"

    if ($logs -match $failurePattern) {
        Write-Host "---- failure log excerpt for $CurrentProfile ----"
        $logs -split "`n" |
            Select-String -Pattern "MediaCleaner|Media Cleaner|PluginLoadException|MissingMethodException|MissingFieldException|TypeLoadException|FileLoadException|FileNotFoundException|Could not load file or assembly|Failed to load assembly|Error loading plugin|Exception|Error|Failed" |
            Select-Object -First 120 |
            ForEach-Object { Write-Host $_.Line }
        return $false
    }

    return $true
}

Invoke-External -FilePath $DockerPath -Arguments @("version")

if (-not $SkipBuild) {
    & $BuildScript -Profile $Profile -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "$BuildScript failed with exit code $LASTEXITCODE"
    }
}

$failedProfiles = New-Object System.Collections.Generic.List[string]

foreach ($currentProfile in $Profile) {
    $currentBuildProfile = Resolve-BuildProfile -CurrentProfile $currentProfile
    $pluginVersion = Get-PluginVersion -BuildProfile $currentBuildProfile
    $pluginArtifacts = @(Get-PluginArtifacts -BuildProfile $currentBuildProfile)
    $image = "jellyfin/jellyfin:$currentProfile"
    $containerName = "media-cleaner-smoke-$($currentProfile.Replace('.', '-'))"
    $profileArtifactRoot = Join-Path $ArtifactsRoot $currentBuildProfile
    $profileSmokeRoot = Join-Path $SmokeRoot $currentProfile
    $configRoot = Join-Path $profileSmokeRoot "config"
    $cacheRoot = Join-Path $profileSmokeRoot "cache"
    $pluginRoot = Join-Path $configRoot "plugins/Media Cleaner_$pluginVersion"

    Write-Host "==> Smoke testing Jellyfin $currentProfile with $image using build profile $currentBuildProfile"

    foreach ($artifact in $pluginArtifacts) {
        if (-not (Test-Path (Join-Path $profileArtifactRoot $artifact))) {
            throw "Missing build artifact for $currentProfile. Expected $profileArtifactRoot/$artifact"
        }
    }

    Remove-ContainerIfExists -Name $containerName
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $profileSmokeRoot
    New-Item -ItemType Directory -Force -Path $pluginRoot, $cacheRoot | Out-Null

    foreach ($artifact in $pluginArtifacts) {
        Copy-Item -Force -Path (Join-Path $profileArtifactRoot $artifact) -Destination $pluginRoot
    }

    Copy-Item -Force -Path (Join-Path $profileArtifactRoot "MediaCleaner.deps.json") -Destination $pluginRoot

    Invoke-External -FilePath $DockerPath -Arguments @("pull", $image)
    Invoke-External -FilePath $DockerPath -Arguments @(
        "run",
        "-d",
        "--name",
        $containerName,
        "-v",
        "$($configRoot):/config",
        "-v",
        "$($cacheRoot):/cache",
        $image
    )

    $started = Wait-JellyfinStartup -ContainerName $containerName -Timeout $TimeoutSeconds
    $logsAreClean = Test-PluginLogs -ContainerName $containerName -CurrentProfile $currentProfile

    if (-not $started) {
        Write-Host "Jellyfin $currentProfile did not reach startup within $TimeoutSeconds seconds."
        $failedProfiles.Add($currentProfile)
    } elseif (-not $logsAreClean) {
        $failedProfiles.Add($currentProfile)
    } else {
        Write-Host "OK $currentProfile"
    }

    if (-not $KeepContainers) {
        Remove-ContainerIfExists -Name $containerName
    }
}

if ($failedProfiles.Count -gt 0) {
    throw "Smoke test failed for profiles: $($failedProfiles -join ', ')"
}

Write-Host "All Jellyfin Docker smoke tests passed: $($Profile -join ', ')"
