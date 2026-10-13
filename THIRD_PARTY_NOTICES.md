# Third-party notices

## BIP-39 English Wordlist

`NinePSharp.Fog.Auth/Resources/bip39-english.txt` is the BIP-39 English wordlist, copied unchanged from `bip-0039/english.txt` in the Bitcoin Improvement Proposals repository (SHA-256 `2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda`). BIP 39 is licensed under the MIT License. The keyfs recovery phrase encodes its storage key with it, and its tests use the reference vectors of trezor/python-mnemonic (MIT).

Reference upstream:
- https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki
- https://github.com/trezor/python-mnemonic

## WebAssembly for .NET

`packages/WebAssembly.2.1.0-fog.2.nupkg` is built from [dotnet-webassembly](https://github.com/RyanLamansky/dotnet-webassembly) at `70c46c0ee78abbdca92490a1881fe509605c5c3d` (tag `v2.1.0`) with the patches in `third_party/dotnet-webassembly` applied, by `scripts/build-wasm-engine.sh`. dotnet-webassembly is Copyright Ryan Lamansky and licensed under the Apache License 2.0; the patches are under the same license.
