# Brave adblock bridge

This crate exposes Brave's [`adblock-rust`](https://github.com/brave/adblock-rust)
engine through a small C ABI used by `VrcCompanion/Services/BraveAdBlockEngine.cs`.

## Rebuild the native bridge

```powershell
cargo build --release --target x86_64-pc-windows-msvc
copy target\x86_64-pc-windows-msvc\release\vrc_companion_adblock.dll ..\VrcCompanion\Native\
```

## Rebuild the bundled filter engine

Download the `default_enabled` desktop lists from
[`brave/adblock-resources`](https://github.com/brave/adblock-resources/blob/master/filter_lists/list_catalog.json)
into one directory as `.txt` files, then run:

```powershell
cargo run --release --bin build_engine -- <list-directory> ..\VrcCompanion\Resources\adblock\brave-engine.bin
```

The checked-in binary is built from the catalog's default-enabled desktop
lists. Mobile and iOS-only lists are excluded.

## Rebuild the Brave resource bundle

Brave's complete scriptlet, redirect, and response-replacement bundle is
published with `adblock-rust`:

```powershell
curl -L https://raw.githubusercontent.com/brave/adblock-rust/master/data/brave/brave-resources.json `
  -o ..\VrcCompanion\Resources\adblock\brave-resources.json
```

This is the resource set Brave ships with its filter lists, including the
uBlock Origin-derived trusted scriptlets (`trusted-replace-xhr-response`,
`trusted-replace-fetch-response`, and their dependencies). The managed engine
loads it with `vrc_adblock_use_resources` after deserializing the compiled
filter engine.

`lists/brave-extra.txt` adds the Spotify web-player audio redirect recommended
by Brave's filter-list maintainers. Include it in the list directory when
rebuilding `brave-engine.bin`.
