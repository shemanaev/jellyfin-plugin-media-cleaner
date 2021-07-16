# Media Cleaner for Jellyfin

Automatically delete played media files after specified amount of time. Works for movies and series.

## Installation

Add repository with my plugins from [jellyfin-plugin-repo](https://github.com/shemanaev/jellyfin-plugin-repo).

## Configuration

Configuration is pretty straightforward at plugin's page.
Here's not so obvious things:

* Media will be considered for deletion after fully played by at least ONE user. To keep things you want use favorites with corresponding settings.
* Favorite episodes aren't kept when "Delete after played" set to season/series.
* All actions taken will be displayed at the Alerts in Dashboard.

## Debugging

Define `JellyfinHome` environment variable pointing to Jellyfin distribution to be able to run debug configuration.
