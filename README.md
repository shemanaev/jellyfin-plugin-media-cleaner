
# Media Cleaner for Jellyfin

Automatically delete or protect Jellyfin media according to configurable rules. Works with movies, episodes, videos, audio and audiobooks; episode rules can also delete complete seasons, complete series, or complete ended series when every required episode matches.

## Installation

Add repository with my plugins from [jellyfin-plugin-repo](https://github.com/shemanaev/jellyfin-plugin-repo).

## Configuration

Media Cleaner is configured via the rules.

### Rules

Rules are split into cleanup rules and protection rules.

* Cleanup rules delete media after it matches their trigger and filters.
* Protection rules never delete media. They exclude matching media from deletion by any cleanup rule.
* Cleanup rules are additive: if several cleanup rules match the same item, the item is planned once.
* Protection rules win regardless of rule order. A protected item suppresses direct deletion, and protected children block season or series deletion cascades.

Each rule has:

* a trigger: played before cutoff, not played since added, or age since added regardless of playback;
* a media scope: one media type or all media;
* optional filters for playback users, favorite state, favorite users, library locations and tags;
* an action: delete or protect.

For episode cleanup rules, the deletion scope controls whether Media Cleaner deletes matching episodes individually, complete seasons, complete series, or complete ended series. Episode and season scopes can keep the first or latest item as an exception.

Played cleanup can require playback by at least one user, the most recent play by any user, or every user. Not-played cleanup can ignore older playback history so old watches do not keep new imports forever.

Tag filters only affect rule matching. Changing a tag in a rule does not rename tags already stored on Jellyfin items; use **Advanced** for library tag maintenance.

### Advanced

The **Advanced** tab contains tools and safety switches that should not be hidden inside ordinary rules:

* **Count playback before Date Added** changes how played and not-played rules handle Jellyfin items whose `LastPlayedDate` is earlier than `DateCreated` / "*Date Added*".
* **Library tag maintenance** previews and then renames a tag on matching Jellyfin library items. It does not update rule definitions automatically.

### Troubleshooting

The **Troubleshooting** tab runs a dry-run cleanup report and opens it in a formatted viewer. It shows the plugin configuration, final delete decisions, planned deletion operations, audit entries, outcome summaries, rule-level decisions and item-level decisions. Use it before enabling risky rules or when a rule does not match the items you expected.

### Jellyfin Date Added behavior

Jellyfin's "*Date added behavior for new content*" setting affects rules that use "*played*" or "*not played*" state, because Media Cleaner compares Jellyfin's `DateCreated` / "*Date Added*" value with the user's `LastPlayedDate`.

There is no single correct setting for every Radarr/Sonarr/download-client setup:

  1. Leave it at the default value ("*Use file creation date*")
      - this can preserve the original media age when Radarr/Sonarr upgrades a file after it was watched;
      - however, recently downloaded files can be deleted on the first cleanup run if your software sets an old filesystem creation date from metadata or from the source file.
  2. Change it to "*Use date scanned into the library*"
      - this helps prevent newly downloaded or re-downloaded media from being deleted immediately based on old playback history;
      - however, if Radarr/Sonarr upgrades a file after it was watched, Jellyfin can set "*Date Added*" later than `LastPlayedDate`. Media Cleaner treats this as ambiguous by default: played cleanup will not use that playback, and not-played cleanup will not delete the item solely because of the date conflict.

Changing Jellyfin's setting does not usually rewrite existing item dates.

If a rule behaves unexpectedly, run Media Cleaner's troubleshooting report and look for entries where `LastPlayedDate` is before Jellyfin "*Date Added*". Those items usually need Jellyfin metadata correction, a different Jellyfin date-added mode for future imports, or an external date-preservation workaround.

If a rule is configured to ignore older playback history, playback before "*Date Added*" is only treated as ambiguous while it is still inside that history window.

Media Cleaner's advanced "*Count playback before Date Added*" setting changes this safety behavior. It counts playback even when `LastPlayedDate` is earlier than "*Date Added*". This can help with upgraded or re-imported media where Jellyfin reset the added date. Enable it only if you accept the tradeoff: newly re-downloaded media can be deleted based on old watch history.

### Played items without a played date

Media Cleaner relies on Jellyfin playback dates when evaluating rules based on played media. Some Jellyfin clients or imported playback data can mark an item as played without setting a `LastPlayedDate`.

When this happens, Media Cleaner cannot know when the item was actually watched. The plugin intentionally does not invent a playback date, because doing so could make cleanup decisions unsafe or misleading.

If played items are not deleted as expected, check the item/user playback data in Jellyfin first. Fix the source client, import process, or use an external repair/sync tool that writes correct Jellyfin playback dates before relying on played-date cleanup rules. One reported workaround is [jellyfin-watch-updater](https://github.com/Simon-Eklundh/jellyfin-watch-updater), which can set `LastPlayedDate` by marking items as played through Jellyfin's API.

## Debugging

Define `JellyfinHome` environment variable pointing to Jellyfin distribution to be able to run debug configuration.

## Jellyfin ABI support

Builds are selected through `JellyfinProfile`. Server profiles are mapped to ABI build profiles in `eng/jellyfin-profiles.json`, so compatible server versions share release artifacts. `Directory.JellyfinProfiles.props` is generated from that JSON and imported by MSBuild.

```powershell
dotnet build MediaCleaner.sln -p:JellyfinProfile=10.10.7
dotnet build MediaCleaner.sln -p:JellyfinProfile=10.11.0
dotnet build MediaCleaner.sln -p:JellyfinProfile=10.11.3
dotnet build MediaCleaner.sln -p:JellyfinProfile=10.11.11
```

Use the packaging helper to generate ABI build artifacts and `build.yaml` files under `artifacts/<build-profile>`:

```powershell
.\eng\build-plugin.ps1 -Configuration Release
```

When Jellyfin changes ABI, add or update a build profile in `eng/jellyfin-profiles.json` with the package version, target ABI, target framework, `versionPatchOffset` and any compile constants needed by `MediaCleaner/Compatibility/JellyfinCompatibility.cs`. Map server versions to build profiles in the same file, then run `.\eng\generate-jellyfin-props.ps1`. Keep direct Jellyfin API changes in the compatibility layer first; the task, filters and controllers should call that layer instead of branching on server versions directly.

To bump the plugin release version, update `baseVersion` and regenerate MSBuild profile properties:

```powershell
.\eng\bump-version.ps1
.\eng\bump-version.ps1 -Version 2.25.0
```

## Contributing

### Generative AI Policy

You're welcome to use "generative AI" coding tools when contributing, however:

* You (the human contributor) should always be reading and validating new code before submitting it to us for review.
* You (the human contributor) should be prepared to communicate with us personally during the PR review process. Copy-pasting to and from a chatbot is unacceptable, unless you're only using it for translation to and from English. If you don't know something then please tell us the prompt you would use for the question, instead of pasting the chatbot's answer - because chatbots are not always correct!
* Please do not allow "AI Agents" to submit Pull Requests by themselves. Human maintainers will make time to review your code, and we expect a human contributor to make time as well.
