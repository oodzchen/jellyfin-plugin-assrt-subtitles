# Jellyfin Assrt.net Subtitle Provider

Fetch and download subtitles from [assrt.net](https://assrt.net/api/doc) directly inside Jellyfin. The plugin supports configuring your own API token (a development token is prefilled) and setting preferred subtitle languages.

## Features
- Searches assrt.net for matching subtitles for movies and TV episodes.
- **Custom Search Keywords in UI**: Injects a custom search keyword input box and clickable auto-query tags directly into Jellyfin Web's "Edit Subtitles" dialog.
- **Metadata-Driven Search (`OriginalTitle` & Tags)**: Automatically reads the media item's `OriginalTitle` (e.g., English/Japanese original name) and supports per-item/per-series custom search keywords via Jellyfin metadata tags (`assrt:Keyword` or `字幕:关键字`).
- **Global Keyword Alias Mappings**: Configure `OriginalName = SearchKeyword` rules in the plugin settings to work across all Jellyfin clients (Web, Desktop, Mobile, TV).
- **Smart Season Pack Unpacking**: Downloads direct subtitle files or extracts nested archives (`.zip`, `.rar`, `.7z`) on the fly, and automatically unpacks & matches full-season subtitle packs to all episodes in the same season folder.
- Configurable API token and preferred ISO 639-3 language codes.
- Ships with a GitHub Actions workflow that builds and bundles the plugin artifact.

## Configuration
1. Install the plugin build in this repository into your Jellyfin server.
2. Open **Dashboard → Plugins → Assrt Subtitles**.
3. Set your assrt.net API token.
4. (Optional) Provide preferred languages as comma separated ISO 639-3 codes, e.g. `zho, eng`.
5. Save and run a subtitle search for any item.

## Local Development
Requirements: .NET SDK 8.0+

```bash
# Restore dependencies
dotnet restore Jellyfin.Plugin.AssrtSubtitles.sln

# Build
dotnet build Jellyfin.Plugin.AssrtSubtitles.sln -c Release

# Publish plugin files into ./publish
dotnet publish src/Jellyfin.Plugin.AssrtSubtitles/Jellyfin.Plugin.AssrtSubtitles.csproj -c Release -o ./publish
```

The published folder contains the plugin DLLs you can copy to your Jellyfin `plugins` directory.

## Continuous Integration
The workflow in `.github/workflows/ci.yml` restores, builds, publishes the plugin, and uploads `assrt-subtitles-plugin.zip` as a build artifact for every push and pull request.
