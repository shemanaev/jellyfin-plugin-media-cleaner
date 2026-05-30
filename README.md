# Media Cleaner for Jellyfin

Media Cleaner deletes played media after a configured time so Jellyfin
servers with limited storage can stay tidy.

This fork keeps the original plugin GUID and adds maintenance fixes and
backwards-compatible improvements for current Jellyfin installations.

## Fork changes

* Jellyfin 10.11 compatibility for user enumeration.
* Playback safety fixes so currently watched media is not selected for
  rolling watched deletion.
* Leaving Soon support with the existing Jellyfin collection plus a
  read-only admin dashboard view.
* Dry-run handling that does not mutate the Leaving Soon collection.
* Optional Radarr and Sonarr deletion delegation for Arr-managed media.

## Installation

Use this plugin repository URL in Jellyfin:

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

The plugin catalogue entry uses the image hosted in this fork at
`images/media-cleaner.png`.

## Compatibility

This release targets Jellyfin `10.11.8.0` and is built for `net9.0`.

## Configuration

Configuration is available from the plugin page in Jellyfin.

Media is considered for deletion after it has been fully played by at
least one user. Use favourites and the related keep settings for media
that should be retained.

Favourite episodes are not kept when `Delete after played` is set to
season or series.

All cleanup actions are shown in Jellyfin dashboard alerts.

For `Delete not played items`, Jellyfin's `Date added behaviour for new
content` affects cleanup timing:

1. `Use file creation date` can cause recently downloaded files to be
   deleted on the first cleanup run if other software modifies file
   creation dates.
2. `Use date scanned into the library` can keep files that were played
   before being updated by other software, because Jellyfin may record a
   later added date after the update.

## Development

Define the `JellyfinHome` environment variable to point at a Jellyfin
distribution before using the debug launch profile.

Run tests with:

```text
dotnet test MediaCleaner.sln
```

## Support

Use this fork's issues page for fork-specific problems:

```text
https://github.com/sym0nd0/jellyfin-plugin-media-cleaner/issues
```

## Credits

This fork is based on
[`shemanaev/jellyfin-plugin-media-cleaner`](https://github.com/shemanaev/jellyfin-plugin-media-cleaner).

The original upstream plugin repository is
[`shemanaev/jellyfin-plugin-repo`](https://github.com/shemanaev/jellyfin-plugin-repo).
