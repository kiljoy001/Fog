# Third-party notices

## BIP-39 English Wordlist

`NinePSharp.Fog.Auth/Resources/bip39-english.txt` is the BIP-39 English wordlist, copied unchanged from `bip-0039/english.txt` in the Bitcoin Improvement Proposals repository (SHA-256 `2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda`). BIP 39 is licensed under the MIT License. The keyfs recovery phrase encodes its storage key with it, and its tests use the reference vectors of trezor/python-mnemonic (MIT).

Reference upstream:
- https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki
- https://github.com/trezor/python-mnemonic

## awk

`NinePSharp.Fog.Commands/Awk*.cs` is a port of 9front's awk (`/sys/src/cmd/awk`), the one true awk of Aho, Weinberger and Kernighan, and `NinePSharp.Fog.Rc.Tests/Data/awk` holds its test programs, their data and their expected output, copied unchanged from `/sys/src/cmd/awk/test`. They are distributed under this notice:

    Copyright (C) Lucent Technologies 1997
    All Rights Reserved

    Permission to use, copy, modify, and distribute this software and
    its documentation for any purpose and without fee is hereby
    granted, provided that the above copyright notice appear in all
    copies and that both that the copyright notice and this
    permission notice and warranty disclaimer appear in supporting
    documentation, and that the name Lucent Technologies or any of
    its entities not be used in advertising or publicity pertaining
    to distribution of the software without specific, written prior
    permission.

    LUCENT DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE,
    INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS.
    IN NO EVENT SHALL LUCENT OR ANY OF ITS ENTITIES BE LIABLE FOR ANY
    SPECIAL, INDIRECT OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
    WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER
    IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
    ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
    THIS SOFTWARE.
