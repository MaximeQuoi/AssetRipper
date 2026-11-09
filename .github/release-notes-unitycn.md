Unofficial fork of [AssetRipper](https://github.com/AssetRipper/AssetRipper) 2.0.0 with **UnityCN (Unity China / Tuanjie) encrypted bundle** support.

## What's new

- Decryption of UnityCN encrypted asset bundles (`0x400` flag, or `0x200` before 2020.3.34 / 2021.3.2 / 2022.1.1).
- **Keys of 24 and 32 characters**: raw 24 characters (AES-192), Base64 (24 characters), 32 hexadecimal characters (AES-128), raw 32 characters (AES-256), as well as raw 16 characters and 48/64 hexadecimal characters. Ambiguous keys are resolved by checking the decrypted bundle signature.
- **Automatic key discovery from Il2CppDumper output**: select `dump.cs` or `stringliteral.json` in *Settings → UnityCN key file*. Every possible key is tried on the encrypted bundles, and the right one(s) are kept, even when a game uses several keys. The keys found are logged and listed on the settings page.
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
