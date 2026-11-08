# AssetRipper

[![](https://img.shields.io/github/downloads/AssetRipper/AssetRipper/total.svg)](https://github.com/AssetRipper/AssetRipper/releases)
[![](https://img.shields.io/github/downloads/AssetRipper/AssetRipper/latest/total.svg)](https://github.com/AssetRipper/AssetRipper/releases/latest)
[![](https://img.shields.io/github/v/release/AssetRipper/AssetRipper)](https://github.com/AssetRipper/AssetRipper/releases/latest)
[![](https://weblate.samboy.dev/widgets/assetripper/-/gui/svg-badge.svg)](http://weblate.samboy.dev/engage/assetripper/)

AssetRipper is a powerful tool for analyzing Unity game files. For example, it enables game developers to:

* Find dependency assets that were accidentally included in their game
* Convert their assets into the native Unity engine format
* Identify code that could not be inlined or trimmed from their build
* Find broken asset references that would cause issues in their game

AssetRipper supports Unity versions from `3.5.0` to `6000.4.X`. However, support quality may vary slightly for different Unity versions.

[Downloads](https://assetripper.com/download.html)

## Premium Edition

There is a premium edition of AssetRipper. This edition includes additional [features and improvements](https://assetripper.com/premium/features/).

## Tips

If you find this tool useful, please consider [tipping](https://assetripper.com/premium/get/) to support development and gain access to [AssetRipper Premium](https://assetripper.com/premium/features/).

## Discord [![](https://img.shields.io/discord/867514400701153281?color=blue&label=AssetRipper)](https://discord.gg/XqXa53W2Yh)

The development of this project has a dedicated [Discord server](https://discord.gg/XqXa53W2Yh).

## Legal Disclaimers

* AssetRipper is licensed under the [GNU General Public License v3.0](LICENSE.md).
* AssetRipper is not sponsored by or affiliated with Unity Technologies or its affiliates.
* "Unity" is a registered trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.
* The [Credits](https://assetripper.github.io/AssetRipper/articles/Credits.html) page contains a list of attributions.

## UnityCN (Unity China / Tuanjie) encrypted bundles — fork addition

This fork decrypts UnityCN encrypted asset bundles (header flag `0x400`, or `0x200` before 2020.3.34 / 2021.3.2 / 2022.1.1).
Set the key in **Settings → UnityCN Key**, or in the `ASSETRIPPER_UNITYCN_KEY` environment variable.

Accepted key formats:

| Format | Example length | AES key |
|---|---|---|
| Hexadecimal | 32 characters | AES-128 |
| Raw key string | 24 characters | AES-192 |
| Base64 | 24 characters | AES-128 |
| Raw key string | 32 characters | AES-256 |
| Raw key string | 16 characters | AES-128 |
| Hexadecimal | 48 / 64 characters | AES-192 / AES-256 |

When a key is ambiguous (e.g. 32 hex characters, or 24 characters that are valid Base64), every interpretation is tried
and the one that decrypts the bundle signature is kept. Prefixes `hex:`, `base64:` and `raw:` force an interpretation.
Several keys can be given, separated by `;` or new lines.

### Finding the keys from an Il2CppDumper dump

Instead of a key, select the `dump.cs` or `stringliteral.json` produced by [Il2CppDumper](https://github.com/Perfare/Il2CppDumper)
in **Settings → UnityCN key file** (or set `ASSETRIPPER_UNITYCN_KEYFILE`).
Every string with a possible key length is extracted (strings next to words such as `key`, `secret`, `crypt` or `aes` first),
then tried on each encrypted bundle: the decrypted signature `#$unity3dchina!@` identifies the right key without false positives.
A game using several keys is handled, since each bundle keeps the key that decrypts it, and keys already found are tried first.
The keys found are written to the log and listed on the settings page after loading.

`stringliteral.json` contains every string literal of the code, so it is the best choice when the key is passed directly to
`AssetBundle.SetAssetBundleDecryptKey("...")`; `dump.cs` only contains the values of `const` fields.
