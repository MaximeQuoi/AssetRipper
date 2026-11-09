Unofficial fork of [AssetRipper](https://github.com/AssetRipper/AssetRipper) 2.0.0 with **UnityCN (Unity China / Tuanjie)** and **NetEase** encrypted bundle support.

## What's new

- Decryption of UnityCN encrypted asset bundles (`0x400` flag, or `0x200` before 2020.3.34 / 2021.3.2 / 2022.1.1).
- **Keys of 24 and 32 characters**: raw 24 characters (AES-192), Base64 (24 characters), 32 hexadecimal characters (AES-128), raw 32 characters (AES-256), as well as raw 16 characters and 48/64 hexadecimal characters. Ambiguous keys are resolved by checking the decrypted bundle signature.
- **Automatic key discovery from Il2CppDumper output**: select `dump.cs` or `stringliteral.json` in *Settings → UnityCN key file*. Every possible key is tried on the encrypted bundles, including **every 32 digit hex key found anywhere in the file** (comments, `0x...` values, longer strings, GUIDs), and the right one(s) are kept, even when a game uses several keys. The keys found are logged and listed on the settings page.
- **Known keys** from [AnimeStudio](https://github.com/Escartem/AnimeStudio) are tried automatically (Punishing Gray Raven, Neural Cloud, Archeland, Higan: Eruthyll...).
- Same decryption as AnimeStudio: `#$manjuuunity*!@` signature and `0x1000` flag supported; for Unity versions up to 2022, the ambiguous `0x200` flag is read both ways and the consistent reading is kept.
- **NetEase protected bundles** (Yidun, `libnesec.so`): the encrypted first block is detected and decrypted automatically, no key needed.
- The open file limit is raised at startup on Linux and macOS, so large games (thousands of bundles) no longer fail with "Too many open files".
- Several manual keys can be given, separated by `;`.
- Environment variables: `ASSETRIPPER_UNITYCN_KEY`, `ASSETRIPPER_UNITYCN_KEYFILE`.

## Downloads

| Platform | File |
|---|---|
| Windows x64 / ARM64 | `AssetRipper_UnityCN_win_x64.zip` / `AssetRipper_UnityCN_win_arm64.zip` |
| Linux x64 / ARM64 | `AssetRipper_UnityCN_linux_x64.tar.xz` / `AssetRipper_UnityCN_linux_arm64.tar.xz` |
| macOS Intel / Apple Silicon | `AssetRipper_UnityCN_mac_x64.tar.xz` / `AssetRipper_UnityCN_mac_arm64.tar.xz` |

`SHA256SUMS.txt` lists the checksums. On macOS, the binaries are not signed: run `xattr -dr com.apple.quarantine <folder>` after extracting if Gatekeeper blocks them.

This build is not affiliated with the AssetRipper project.
