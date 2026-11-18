# Third-party notices

## Brave adblock-rust

The in-app Shields engine is built from
[`brave/adblock-rust`](https://github.com/brave/adblock-rust), licensed under
the Mozilla Public License 2.0 (MPL-2.0). The wrapper source is in
`VrcCompanion.NativeAdBlock/`; the compiled bridge is
`VrcCompanion/Native/vrc_companion_adblock.dll`.

## Filter lists

`VrcCompanion/Resources/adblock/brave-engine.bin` is a serialized engine built
from Brave's default filter-list catalog
([`brave/adblock-resources`](https://github.com/brave/adblock-resources)),
including:

- EasyList — https://easylist.to/
- EasyPrivacy — https://easylist.to/
- uBlock Origin filters and uBlock Origin privacy filters —
  https://github.com/uBlockOrigin/uAssets
- URLhaus Malicious URL Blocklist — https://urlhaus.abuse.ch/
- Brave-specific, Brave Unbreak, Brave Social, Brave First-Party, SugarCoat,
  and Brave cookie-notice rules — https://github.com/brave/adblock-lists

The lists remain subject to their respective upstream licenses and attribution
requirements. The catalog is fetched from Brave's repository when the bundled
engine is rebuilt.

## Brave adblock-resources and uBlock Origin resources

`VrcCompanion/Resources/adblock/brave-resources.json` is the complete resource
bundle published with [`brave/adblock-rust`](https://github.com/brave/adblock-rust)
(MPL-2.0). It contains Brave's resources plus the uBlock Origin-derived
scriptlets and redirects used by Brave's filter rules. The uBlock Origin
resources are licensed under GPL-3.0; see <https://github.com/gorhill/uBlock>.
The bundle is passed to the native `adblock-rust` engine at startup.
