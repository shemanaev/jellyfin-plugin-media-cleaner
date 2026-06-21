
# Media Cleaner for Jellyfin

Automatically delete played media files after specified amount of time. Works for movies and series.

## Installation

Add repository with my plugins from [jellyfin-plugin-repo](https://github.com/shemanaev/jellyfin-plugin-repo).

## Configuration

Configuration is pretty straightforward at plugin's page.
Here's not so obvious things:

* Media will be considered for deletion after fully played by at least ONE user. To keep things you want use favorites with corresponding settings.
* Favorite episodes aren't kept when "*Delete after played*" set to season/series.
* All actions taken will be displayed at the Alerts in Dashboard.

For the correct operation of the "*Delete not played items*" function, there are two possible configuration options for "*Date added behavior for new content*" in Jellyfin. Each option has its own drawbacks, depending on your setup:

  1. Leave it at the default value ("*Use file creation date*")
      - recently downloaded files can be deleted on the first cleanup run if your software (Radarr, download client, etc...) modifies the file creation date (like sets it from metadata of download or something).
  3. Change it to "*Use date scanned into the library*"
      - when using software that can update files at any time (Radarr, etc...), it is possible that the file will be updated after being played, and thus the creation date in Jellyfin will also be updated. It will not be deleted because the creation date will be later than the watch date.

![Снимок экрана 2023-06-08 091645](https://github.com/shemanaev/jellyfin-plugin-media-cleaner/assets/1058537/2ecfd52c-e9da-425c-ae08-60494f5aedc8)

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
