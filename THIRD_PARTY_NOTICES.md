# Third-party notices

## Minecraft Ring and minecraft-crossover-bridge (MIT)

UltraRing is built on [Minecraft Ring](https://github.com/siddoff/Minecraft-Ring)
(Copyright (c) 2026 siddoff, Copyright (c) 2026 justbustin), a Windows adaptation of the Elden Ring part of
[minecraft-crossover-bridge](https://github.com/justbustin/minecraft-crossover-bridge) (Copyright (c) 2026 justbustin).
Both are released under the MIT License, like UltraRing.

What UltraRing takes from them:

- **The Elden Ring host DLLs** (`dinput8.dll`, `erbridge_core.dll`) are compiled, unchanged, from Minecraft Ring's
  `bridge-base/elden-ring/er-bridge` sources at commit `711015afa67ab25c7b9597c68038718d1cef322e`. `tools/Build.ps1`
  fetches that commit into `external/minecraft-ring`; the sources are not copied into this repository.
- **The shared-memory protocol**: the C# transcription (now in the ULTRAKILL Crossover Bridge kit, see below) is a transcription of Minecraft Ring's
  `bridge_protocol.h` (layout, offsets and semantics), so the unchanged host DLL can talk to ULTRAKILL.
- **The launcher and installer flow** (`Install.ps1`, `Launch.ps1`, `Restore.ps1`) is adapted from Minecraft Ring's
  scripts, and the guest behaviour (recall, shared life, F8 switching, input window, frame passthrough, terrain
  sampling through the ray mailbox) follows its Fabric mod.

Their license notices, which also apply to the compiled host DLLs:

```
MIT License

Copyright (c) 2026 siddoff
Copyright (c) 2026 justbustin (upstream bridge)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## MinHook (BSD 2-Clause)

The host DLLs statically include [MinHook](https://github.com/TsudaKageyu/minhook) and the Hacker Disassembler
Engine, bundled in Minecraft Ring's sources. Their license is in
`external/minecraft-ring/bridge-base/elden-ring/er-bridge/third_party/minhook/LICENSE.txt` once fetched, and must be
kept when distributing the compiled DLLs.

## BepInEx and its dependencies

ULTRAKILL loads the plugin through [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.5 (LGPL-2.1), which bundles
UnityDoorstop, HarmonyX, MonoMod and Mono.Cecil under their own licenses. `tools/Build.ps1` downloads the official
release; it is not part of this repository.

## Games

ULTRAKILL (New Blood Interactive / Arsi "Hakita" Patala) and Elden Ring (FromSoftware / Bandai Namco) are not
included. You need your own copies. Game names and assets belong to their owners; the MIT License covers the bridge
code only and grants no rights to either game.

## ULTRAKILL Crossover Bridge (MIT)

The ULTRAKILL guest, the protocol library, the host SDK and the fake host are in
[ULTRAKILL Crossover Bridge](https://github.com/Themetralla3000/ultrakill-crossover-bridge) (Copyright (c) 2026 Arnau Encinas, same author as UltraRing, MIT License),
included as the git submodule `bridge/`. Its own THIRD_PARTY_NOTICES.md covers what it uses (BepInEx, HarmonyX, ...).
