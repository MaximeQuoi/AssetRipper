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

## Models in the direct export (new in 2.0.0-unitycn.6)

- **Skinned meshes in GLB files**: the primary content (direct) export now writes the meshes of `SkinnedMeshRenderer`s with their skeleton (joints and inverse bind matrices from the bind poses). Characters and animated models no longer come out invisible, as a bare bone hierarchy. When the skeleton cannot be used, the mesh is still exported in its bind pose.
- Node names are made unique in each model, as glTF skins require.
- `_BumpMap` / `_NormalMap` are exported as normal maps, `_BaseMap` / `_Albedo` as base color.

## Unity projects of IL2CPP games (new in 2.0.0-unitycn.5)

- **DumpCsToAssemblies** (in the archives, folder `DumpCsToAssemblies`): rebuilds compilable stub assemblies from Il2CppDumper's `dump.cs` when `global-metadata.dat` cannot be read (encrypted). Put them in the game's `Managed` folder: the exported project compiles, MonoBehaviours keep their fields and scripts, with empty method bodies that never throw.
  `DumpCsToAssemblies <dump.cs> <Unity editor Contents (macOS) or Editor/Data folder> <output> [--sources] [--real <folder>]`.
  Extension methods, default values, auto-properties and singletons (`Instance`) are kept; `--real` replaces the stubs of packages by their real assemblies (UnityEngine.UI, Timeline...).
- **Shaders translated from OpenGL ES**: the GLES3 programs of a shader are translated to HLSL, so the exported shaders render instead of being replaced by dummies; shaders that cannot be translated keep their render states (blending, depth, culling, stencil) in the dummy.
- **Deterministic GUIDs**: an exported asset gets the same GUID in every export (derived from its file and path ID), so a large game can be exported in parts and merged into one project.
- TextAssets with a non-Unity extension (`.skel`, `.atlas`) are exported with a usable one, so Spine data keeps working.
- The stubs of UI components that draw no mesh no longer render white rectangles.

## Downloads

| Platform | File |
|---|---|
| Windows x64 / ARM64 | `AssetRipper_UnityCN_win_x64.zip` / `AssetRipper_UnityCN_win_arm64.zip` |
| Linux x64 / ARM64 | `AssetRipper_UnityCN_linux_x64.tar.xz` / `AssetRipper_UnityCN_linux_arm64.tar.xz` |
| macOS Intel / Apple Silicon | `AssetRipper_UnityCN_mac_x64.tar.xz` / `AssetRipper_UnityCN_mac_arm64.tar.xz` |

`SHA256SUMS.txt` lists the checksums. On macOS, the binaries are not signed: run `xattr -dr com.apple.quarantine <folder>` after extracting if Gatekeeper blocks them.

This build is not affiliated with the AssetRipper project.
