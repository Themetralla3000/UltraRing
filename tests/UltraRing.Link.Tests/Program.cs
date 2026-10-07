using System;
using System.Runtime.CompilerServices;
using UltraRing.Link;

// Layout checks mirroring the static_asserts at the end of bridge_protocol.h.
int failures = 0;
void Check(string name, int actual, int expected)
{
    bool ok = actual == expected;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {actual:X} (expected {expected:X})");
}
unsafe
{
    Check("ErmcHeader", sizeof(ErmcHeader), 0xC0);
    Check("ErmcGameState", sizeof(ErmcGameState), 0x114);
    Check("ErmcControl", sizeof(ErmcControl), 0x64);
    Check("ErmcHunterEvents", sizeof(ErmcHunterEvents), 0x30);
    Check("ErmcRayHeader", sizeof(ErmcRayHeader), 0x20);
    Check("ErmcRay", sizeof(ErmcRay), 24);
    Check("ErmcRayHit", sizeof(ErmcRayHit), 32);
    Check("ErmcEntity", sizeof(ErmcEntity), 0x80);
    Check("ErmcEntityTableHeader", sizeof(ErmcEntityTableHeader), 0x10);
    Check("ErmcDamage", sizeof(ErmcDamage), 0x20);
    Check("ErmcDamageQueueHeader", sizeof(ErmcDamageQueueHeader), 0x10);
    Check("ErmcTerrainContact", sizeof(ErmcTerrainContact), 32);
    Check("ErmcTerrainContactsHeader", sizeof(ErmcTerrainContactsHeader), 40);
    Check("ErmcCollisionControl", sizeof(ErmcCollisionControl), 48);
    Check("ErmcPassage", sizeof(ErmcPassage), 0x20);
    Check("ErmcPlatformCell", sizeof(ErmcPlatformCell), 32);
    Check("ErmcEnvironment", sizeof(ErmcEnvironment), 24);
    Check("ErmcFramesHeader", sizeof(ErmcFramesHeader), 0x18);
    Check("ErmcFrameHeader", sizeof(ErmcFrameHeader), 0x38);
    ErmcHeader h = default;
    Check("hostPrompt offset", (int)((byte*)h.hostPrompt - (byte*)&h), 0x80);
    Check("hostLife offset", (int)((byte*)&h.hostLife - (byte*)&h), 0x58);
    ErmcGameState s = default;
    Check("stageId offset", (int)((byte*)&s.stageId - (byte*)&s), 0x7C);
    Check("supportPos offset", (int)((byte*)s.supportPos - (byte*)&s), 0x108);
    ErmcControl c = default;
    Check("hunterYawDeg offset", (int)((byte*)&c.hunterYawDeg - (byte*)&c), 0x58);
}
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
return failures == 0 ? 0 : 1;
