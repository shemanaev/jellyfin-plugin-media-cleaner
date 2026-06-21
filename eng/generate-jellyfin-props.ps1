param(
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProfilesPath = Join-Path $PSScriptRoot "jellyfin-profiles.json"
$resolvedOutputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $RepoRoot "Directory.JellyfinProfiles.props"
} elseif ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $RepoRoot $OutputPath
}

function ConvertTo-XmlAttribute {
    param([string]$Value)

    return [System.Security.SecurityElement]::Escape($Value)
}

$profilesConfig = Get-Content -Raw -Path $ProfilesPath | ConvertFrom-Json
$pluginBaseVersion = ConvertTo-XmlAttribute ([string]$profilesConfig.baseVersion)
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add("<Project>")
$lines.Add("  <!-- Generated from eng/jellyfin-profiles.json. Run eng/generate-jellyfin-props.ps1 to update. -->")
$lines.Add("")
$lines.Add("  <PropertyGroup>")
$lines.Add("    <PluginBaseVersion Condition=`"'`$(PluginBaseVersion)' == ''`">$pluginBaseVersion</PluginBaseVersion>")
$lines.Add("  </PropertyGroup>")

foreach ($profile in @($profilesConfig.serverProfiles)) {
    $profileName = ConvertTo-XmlAttribute ([string]$profile.profile)
    $buildProfile = ConvertTo-XmlAttribute ([string]$profile.buildProfile)

    $lines.Add("")
    $lines.Add("  <PropertyGroup Condition=`"'`$(JellyfinBuildProfile)' == '' And '`$(JellyfinProfile)' == '$profileName'`">")
    $lines.Add("    <JellyfinBuildProfile>$buildProfile</JellyfinBuildProfile>")
    $lines.Add("  </PropertyGroup>")
}

foreach ($profile in @($profilesConfig.buildProfiles)) {
    $profileName = ConvertTo-XmlAttribute ([string]$profile.profile)
    $packageVersion = ConvertTo-XmlAttribute ([string]$profile.packageVersion)
    $targetAbi = ConvertTo-XmlAttribute ([string]$profile.targetAbi)
    $targetFramework = ConvertTo-XmlAttribute ([string]$profile.targetFramework)
    $versionPatchOffset = ConvertTo-XmlAttribute ([string]$profile.versionPatchOffset)
    $constants = ConvertTo-XmlAttribute (($profile.constants | ForEach-Object { [string]$_ }) -join ";")

    $lines.Add("")
    $lines.Add("  <PropertyGroup Condition=`"'`$(JellyfinBuildProfile)' == '$profileName'`">")
    $lines.Add("    <JellyfinPackageVersion Condition=`"'`$(JellyfinPackageVersion)' == ''`">$packageVersion</JellyfinPackageVersion>")
    $lines.Add("    <JellyfinTargetAbi Condition=`"'`$(JellyfinTargetAbi)' == ''`">$targetAbi</JellyfinTargetAbi>")
    $lines.Add("    <JellyfinTargetFramework>$targetFramework</JellyfinTargetFramework>")
    $lines.Add("    <JellyfinVersionPatchOffset>$versionPatchOffset</JellyfinVersionPatchOffset>")
    $lines.Add("    <PluginVersion Condition=`"'`$(PluginVersion)' == ''`">`$(PluginBaseVersion).$versionPatchOffset</PluginVersion>")
    $lines.Add("    <JellyfinCompatibilityConstants>$constants</JellyfinCompatibilityConstants>")
    $lines.Add("  </PropertyGroup>")
}

$lines.Add("</Project>")

$resolvedOutputDir = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($resolvedOutputDir)) {
    New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
}

[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    (($lines -join "`n") + "`n"),
    [System.Text.Encoding]::ASCII)
