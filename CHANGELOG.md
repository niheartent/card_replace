# Changelog

## v0.1.2

First usable release.

### Added

- Generate a ready-to-install `card_replace` mod from a target folder.
- Read Card Art Editor `.cardartpack.json` files.
- Convert `animated_gif` entries into Godot `AnimatedTexture` resources during generation.
- Read compatible unencrypted card art mod folders, `.zip`, and `.pck` files.
- Merge duplicate card art by `priority.json`; higher priority wins.
- Output `manifest.final.cardreplace` and `conflicts.report.cardreplace`.
- Show generated pack information in the RitsuLib settings page with English and Chinese localization.

### Requirements

- .NET 9 SDK.
- Godot 4.5.1 Mono console build.
- Slay the Spire 2 `0.107.1` or newer.
- STS2-RitsuLib `0.4.51` or newer.
