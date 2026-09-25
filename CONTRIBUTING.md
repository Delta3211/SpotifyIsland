# Contributing to SpotifyIsland

## Local setup

1. Install the .NET 8 SDK on Windows 10 or Windows 11.
2. Clone the repository and run `dotnet build SpotifyIsland.csproj`.
3. Start the app with `dotnet run --project SpotifyIsland.csproj`.

## Before opening a pull request

1. Keep changes focused and preserve the tray-first behavior.
2. Run `dotnet build SpotifyIsland.csproj` without warnings or errors.
3. Test the compact island, expanded island, settings panel, and tray menu when changing UI or startup code.
4. Update `CHANGELOG.md` for user-visible changes.

## Release builds

Push a version tag such as `v1.1.0`. GitHub Actions publishes a self-contained Windows executable and attaches it to the matching GitHub Release.
