
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
