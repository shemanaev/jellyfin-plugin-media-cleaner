param(
    [Parameter(Mandatory = $true)]
    [string]$Profile,
    [string]$OutputPath = "build.yaml"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot "MediaCleaner/MediaCleaner.csproj"

function Invoke-Process {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output)
    }
}

function Get-MsBuildProperty {
    param(
        [string]$Name
    )

    $result = Invoke-Process -FilePath "dotnet" -Arguments @(
        "msbuild",
        $ProjectPath,
        "-nologo",
        "-p:JellyfinProfile=$Profile",
        "-getProperty:$Name"
    )

    return ($result.Output -join "`n").Trim()
}

function ConvertTo-YamlQuoted {
    param(
        [string]$Value
    )

    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function ConvertTo-YamlBlock {
    param(
        [string]$Value
    )

    $lines = $Value -split "\r?\n"
    return ($lines | ForEach-Object { "  $_" }) -join "`n"
}

function Get-LastReachableTag {
    $result = Invoke-Process -FilePath "git" -Arguments @("describe", "--tags", "--abbrev=0", "HEAD") -AllowFailure
    if ($result.ExitCode -ne 0) {
        return $null
    }

    return ($result.Output | Select-Object -First 1)
}

function Get-PreviousReachableTag {
    $result = Invoke-Process -FilePath "git" -Arguments @("describe", "--tags", "--abbrev=0", "HEAD^") -AllowFailure
    if ($result.ExitCode -ne 0) {
        return $null
    }

    return ($result.Output | Select-Object -First 1)
}

function Get-ChangelogStartRef {
    $tagsAtHead = Invoke-Process -FilePath "git" -Arguments @("tag", "--points-at", "HEAD") -AllowFailure
    if ($tagsAtHead.ExitCode -eq 0 -and @($tagsAtHead.Output).Count -gt 0) {
        return Get-PreviousReachableTag
    }

    return Get-LastReachableTag
}

function Get-Changelog {
    $startRef = Get-ChangelogStartRef
    $logArgs = @("log", "--no-merges", "--reverse", "--format=%s")
    if (-not [string]::IsNullOrWhiteSpace($startRef)) {
        $logArgs += "$startRef..HEAD"
    }

    $result = Invoke-Process -FilePath "git" -Arguments $logArgs -AllowFailure
    if ($result.ExitCode -ne 0) {
        return "Maintenance release"
    }

    $entries = @($result.Output) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Where-Object { $_ -notmatch '^\s*Bump version\s*$' }

    if ($entries.Count -eq 0) {
        return "Maintenance release"
    }

    return ($entries | ForEach-Object { "- $_" }) -join "`n"
}

$pluginName = Get-MsBuildProperty -Name "PluginName"
$pluginGuid = Get-MsBuildProperty -Name "PluginGuid"
$pluginVersion = Get-MsBuildProperty -Name "PluginVersion"
$targetAbi = Get-MsBuildProperty -Name "JellyfinTargetAbi"
$framework = Get-MsBuildProperty -Name "JellyfinTargetFramework"
$overview = Get-MsBuildProperty -Name "PluginOverview"
$description = Get-MsBuildProperty -Name "PluginDescription"
$category = Get-MsBuildProperty -Name "PluginCategory"
$owner = Get-MsBuildProperty -Name "PluginOwner"
$artifacts = Get-MsBuildProperty -Name "PluginArtifacts"
$changelog = Get-Changelog
$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $RepoRoot $OutputPath
}
$resolvedOutputDir = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($resolvedOutputDir)) {
    New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
}

$buildYaml = @"
---
name: $(ConvertTo-YamlQuoted $pluginName)
guid: $(ConvertTo-YamlQuoted $pluginGuid)
version: $(ConvertTo-YamlQuoted $pluginVersion)
targetAbi: $(ConvertTo-YamlQuoted $targetAbi)
framework: $(ConvertTo-YamlQuoted $framework)
overview: $(ConvertTo-YamlQuoted $overview)
description: >
$(ConvertTo-YamlBlock $description)
category: $(ConvertTo-YamlQuoted $category)
owner: $(ConvertTo-YamlQuoted $owner)
artifacts:
$(($artifacts -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { "- $(ConvertTo-YamlQuoted $_.Trim())" }) -join "`n")
changelog: >
$(ConvertTo-YamlBlock $changelog)
"@

Set-Content -Path $resolvedOutputPath -Value $buildYaml -Encoding ASCII
