# Warning: AI-developed fork

This fork contains commits developed with AI assistance. Review the changes before relying on them outside a personal environment.

This is a personal fork of `shemanaev/jellyfin-plugin-media-cleaner`. It is not intended as criticism of the original developer or their work. I made this fork because I rely on this plugin to manage limited storage space, and the upstream project currently has unresolved issues that affect my setup.

The main reasons for this fork are:

* A critical compatibility issue in current Jellyfin versions. Upstream PR #102 documents that Jellyfin 10.11.x removed `IUserManager.Users`, causing the played media cleanup task to crash at runtime with `MissingMethodException`. This fork includes compatibility handling for current Jellyfin.
* Missing Leaving Soon functionality. Upstream PR #91, opened on 19 December 2025, adds a Leaving Soon collection for issue #68 and issue #86, but it has not been merged upstream.
* A playback safety fix from upstream PR #104, which protects media that are currently being watched from being treated as deletion candidates in the rolling watched mode.

I am concerned that upstream may now be inactive: upstream `master` was last pushed on 19 December 2025, while the PRs above remain open as of 30 May 2026. This fork exists to keep the plugin usable for my own storage-management needs.

Enhancements in this fork include:

* Deletion of Arr-managed media through Radarr and Sonarr where possible, rather than direct Jellyfin deletion. This gives a cleaner end-to-end process, including Arr-side file deletion and import exclusion or monitoring behaviour.
* Updated Leaving Soon support that keeps the existing Jellyfin collection for normal clients and adds a read-only admin dashboard view.
* Dry-run handling that avoids mutating the Leaving Soon collection.
* Compatibility and safety fixes from the open upstream PRs noted above.

<div style="page-break-after: always;"></div>

# Media Cleaner for Jellyfin

Automatically delete played media files after specified amount of time. Works for movies and series.

## Installation

Add this plugin repository URL in Jellyfin:

```text
https://raw.githubusercontent.com/sym0nd0/jellyfin-plugin-media-cleaner/master/manifest.json
```

1. Open Jellyfin Dashboard.
2. Go to `Plugins`.
3. Go to `Repositories`.
4. Add the repository URL above.
5. Save.
6. Go to `Catalogue`.
7. Find `Media Cleaner`.
8. Install it.
9. Restart Jellyfin if prompted.

Raise issues for this fork in this repository. Pull requests are welcome.

<br>

## Configuration

Configuration is pretty straightforward at plugin's page.
Here's not so obvious things:

* Media will be considered for deletion after fully played by at least ONE user. To keep things you want use favourites with corresponding settings.
* Favourite episodes aren't kept when "*Delete after played*" set to season/series.
* All actions taken will be displayed at the Alerts in Dashboard.

For the correct operation of the "*Delete not played items*" function, there are two possible configuration options for "*Date added behaviour for new content*" in Jellyfin. Each option has its own drawbacks, depending on your setup:

  1. Leave it at the default value ("*Use file creation date*")
      - recently downloaded files can be deleted on the first cleanup run if your software (Radarr, download client, etc...) modifies the file creation date (like sets it from metadata of download or something).
  3. Change it to "*Use date scanned into the library*"
      - when using software that can update files at any time (Radarr, etc...), it is possible that the file will be updated after being played, and thus the creation date in Jellyfin will also be updated. It will not be deleted because the creation date will be later than the watch date.

## Debugging

Define `JellyfinHome` environment variable pointing to Jellyfin distribution to be able to run debug configuration.
