using System.Runtime.InteropServices;

namespace UltraRing.Link
{
    /// <summary>
    /// C# mirror of Minecraft Ring's er-bridge/include/bridge_protocol.h (pinned commit 711015a).
    /// The Elden Ring host DLL is used unchanged, so every offset and size here must match it
    /// byte for byte. Everything is little-endian; seqlocked blocks: odd seq = write in progress.
    /// </summary>
    public static class Protocol
    {
        public const uint Magic = 0x434D484Du;          // "MHMC"
        public const uint Version = 1u;
        public const int ShmSize = 8 * 1024 * 1024;

        public const int OffHeader = 0x000000;
        public const int OffState = 0x000100;
        public const int OffControl = 0x000800;
        public const int OffHunter = 0x000A00;
        public const int OffEnvironment = 0x000B00;
        public const int OffCmd = 0x001000;
        public const int OffRays = 0x100000;
        public const int OffEntities = 0x200000;
        public const int OffDamage = 0x280000;
        public const int OffPassages = 0x300000;
        public const int OffPlatforms = 0x310000;
        public const int OffContacts = 0x320000;
        public const int OffCollisionControl = 0x350000;

        public const int MaxRays = 8192;
        public const int RaysOffRays = 0x20;
        public const int RaysOffHits = RaysOffRays + MaxRays * 24;
        public const uint RaysCameraFilter = 1u << 0;
        public const uint RaysCustomFilter = 1u << 1;

        public const int MaxEntities = 256;
        public const int DamageRing = 256;
        public const int MaxPassages = 64;
        public const int MaxPlatformCells = 169;
        public const int MaxContacts = 4096;

        // ErmcGameState.flags
        public const uint StateCameraValid = 1u << 0;
        public const uint StatePlayerValid = 1u << 1;
        public const uint StateWindowValid = 1u << 2;
        public const uint StateCamOverridden = 1u << 3;
        public const uint StateMatricesValid = 1u << 4;
        public const uint StateWindowFocused = 1u << 5;
        public const uint StateCompositing = 1u << 6;
        public const uint StatePlayerDead = 1u << 7;
        public const uint StateHostBusy = 1u << 8;
        public const uint StateSupportValid = 1u << 9;

        // ErmcControl.flags
        public const uint CtrlOverrideCamera = 1u << 0;
        public const uint CtrlMoveHunter = 1u << 1;
        public const uint CtrlHideHunter = 1u << 2;
        public const uint CtrlCaptureDepth = 1u << 3;
        public const uint CtrlComposite = 1u << 4;
        public const uint CtrlNoDepthTest = 1u << 5;
        public const uint CtrlDebugDepth = 1u << 6;
        public const uint CtrlNoRelight = 1u << 7;
        public const uint CtrlGrounded = 1u << 8;
        public const uint CtrlFlying = 1u << 9;

        // ErmcEntity.kind / flags
        public const uint EntLargeMonster = 1u;
        public const uint EntSmallMonster = 2u;
        public const uint EntOther = 3u;
        public const uint EntityDead = 1u << 0;
        public const uint EntityWorldBox = 1u << 1;

        // ErmcDamage.flags
        public const uint DamageCritical = 1u << 0;
        public const uint DamageOutward = 1u << 1;
        public const uint DamageNotByPlayer = 1u << 2;
        public const uint DamageWorldRay = 1u << 3;

        // ErmcTerrainContact.kind
        public const uint ContactFloor = 1u;
        public const uint ContactWall = 2u;
        public const uint ContactCeiling = 3u;
        public const uint ContactClear = 4u;

        public const uint EnvTime = 1u;
        public const uint EnvWeather = 2u;

        // frames.shm
        public const uint FramesMagic = 0x524D484Du;    // "MHMR"
        public const uint FramesVersion = 3u;
        public const int FrameMaxW = 3840;
        public const int FrameMaxH = 2160;
        public const int FrameSlots = 3;
        public const int FrameHdr = 0x100;
        public const long FrameSlotSize = FrameHdr + (long)FrameMaxW * FrameMaxH * 16;
        public const long FramesFileSize = 0x1000 + FrameSlots * FrameSlotSize;
        public const uint FrameWorld = 1u << 0;
        public const uint FrameGui = 1u << 1;
        public const uint FrameHand = 1u << 2;
        public const uint FrameGpu = 1u << 3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcHeader
    {
        public uint magic;              // 0x00
        public uint version;            // 0x04
        public uint size;               // 0x08
        public uint reserved0;          // 0x0C
        public ulong hostHeartbeat;     // 0x10
        public ulong mcHeartbeat;       // 0x18 (guest heartbeat)
        public uint hostPid;            // 0x20
        public uint mcPid;              // 0x24
        public ulong hostStartMs;       // 0x28
        public ulong mcStartMs;         // 0x30
        public uint coreReloadReq;      // 0x38
        public uint coreReloadAck;      // 0x3C
        public uint coreGeneration;     // 0x40
        public int coreStatus;          // 0x44
        public uint mcSwitchReq;        // 0x48 host -> guest: F8 pressed in the host
        public uint hostFocusReq;       // 0x4C guest -> host: bring the host window forward
        public ulong hostTaskPage;      // 0x50
        public uint hostLife;           // 0x58
        public uint mcDeaths;           // 0x5C
        public uint hostDeaths;         // 0x60
        public uint debugFlags;         // 0x64
        public ulong hostPresentPage;   // 0x68
        public uint mcActionReq;        // 0x70
        public uint hostActionAck;      // 0x74
        public int hostActionResult;    // 0x78
        public uint hostPromptSeq;      // 0x7C
        public fixed byte hostPrompt[64]; // 0x80
    }                                   // 0xC0

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcGameState
    {
        public uint seq;                // 0x00
        public uint flags;              // 0x04
        public ulong frame;             // 0x08
        public fixed float camPos[3];   // 0x10
        public fixed float camTarget[3];// 0x1C
        public fixed float camUp[3];    // 0x28
        public float fovYDeg;           // 0x34
        public float nearZ;             // 0x38
        public float farZ;              // 0x3C
        public float aspect;            // 0x40
        public float unitsPerMeter;     // 0x44
        public fixed float playerPos[3];// 0x48
        public fixed float playerQuat[4];// 0x54
        public int winX, winY, winW, winH; // 0x64
        public uint bbW, bbH;           // 0x74
        public uint stageId;            // 0x7C
        public fixed float view[16];    // 0x80
        public fixed float proj[16];    // 0xC0
        public uint supportEpoch;       // 0x100
        public float supportTravelY;    // 0x104
        public fixed float supportPos[3]; // 0x108
    }                                   // 0x114

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcControl
    {
        public uint seq;                // 0x00
        public uint flags;              // 0x04
        public ulong mcFrame;           // 0x08 pose id
        public fixed float camPos[3];   // 0x10
        public fixed float camTarget[3];// 0x1C
        public fixed float camUp[3];    // 0x28
        public float fovYDeg;           // 0x34
        public fixed float hunterPos[3];// 0x38 stand-in feet
        public uint poseLag;            // 0x44
        public uint depthIndex;         // 0x48
        public float lightGain;         // 0x4C
        public float lightMin;          // 0x50
        public float fogStrength;       // 0x54
        public float hunterYawDeg;      // 0x58 Minecraft yaw convention
        public uint supportEpoch;       // 0x5C
        public float supportTravelY;    // 0x60
    }                                   // 0x64

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcHunterEvents
    {
        public uint seq;                // 0x00
        public uint hitCount;           // 0x04
        public float totalDamage;       // 0x08
        public float lastDamage;        // 0x0C
        public fixed float lastHitFrom[3]; // 0x10
        public float hunterMaxHp;       // 0x1C
        public ulong lastHitFrame;      // 0x20
        public uint lastHitKind;        // 0x28
        public float totalStatusDamage; // 0x2C
    }                                   // 0x30

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcEnvironment
    {
        public uint seq, flags, timeRevision, dayTicks, weatherRevision, weather;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ErmcRayHeader
    {
        public uint reqSeq, respSeq, count, flags, processed, filterA, filterB, filterC;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcRay
    {
        public fixed float start[3];
        public fixed float end[3];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcRayHit
    {
        public fixed float pos[3];
        public fixed float normal[3];
        public uint hit;
        public uint attr;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcEntity
    {
        public ulong id;                // 0x00
        public uint kind;               // 0x08
        public uint emId;               // 0x0C
        public fixed float pos[3];      // 0x10 feet
        public fixed float quat[4];     // 0x1C
        public fixed float boxCenter[3];// 0x2C
        public fixed float boxHalf[3];  // 0x38
        public float hp;                // 0x44
        public float maxHp;             // 0x48
        public uint flags;              // 0x4C
        public fixed byte name[48];     // 0x50
    }                                   // 0x80

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ErmcEntityTableHeader
    {
        public uint seq;
        public uint count;
        public ulong frame;             // entities follow at +0x10
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcDamage
    {
        public ulong id;
        public float amount;
        public fixed float hitPos[3];
        public uint flags;
        public uint reserved;
    }                                   // 0x20

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ErmcDamageQueueHeader
    {
        public uint write;
        public uint read;
        public uint reserved0, reserved1; // ring follows at +0x10
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcTerrainContact
    {
        public fixed float min[3];
        public fixed float max[3];
        public uint kind;
        public uint reserved;
    }                                   // 32

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcTerrainContactsHeader
    {
        public uint seq;
        public uint count, zone, valid;
        public fixed float origin[3];
        public float previousFeetY;
        public float previousFeetX, previousFeetZ; // valid bit1: 40-byte header
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ErmcCollisionControl
    {
        public uint seq;
        public uint flags, zone;
        public float previousFeetX;
        public fixed float feet[3];
        public float previousFeetY;
        public fixed float velocity[3];
        public float previousFeetZ;
    }                                   // 48

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ErmcPassage
    {
        public float x, y, z, yaw, halfWidth, halfDepth, height;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ErmcPlatformCell
    {
        public float x, z, floor, previousFloor, clearLow, clearHigh;
        public uint flags, reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ErmcFramesHeader
    {
        public uint magic;              // 0x00
        public uint version;            // 0x04
        public uint latestSlot;         // 0x08
        public uint reserved;           // 0x0C
        public ulong latestFrameId;     // 0x10
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ErmcFrameHeader
    {
        public uint seq;                // 0x00 odd while the guest writes the slot
        public uint width;              // 0x04
        public uint height;             // 0x08
        public uint flags;              // 0x0C Protocol.Frame*
        public ulong frameId;           // 0x10
        public ulong poseId;            // 0x18 = ErmcControl.mcFrame of this frame's camera
        public float mcNear;            // 0x20
        public float mcFar;             // 0x24
        public float fovYDeg;           // 0x28
        public float aspect;            // 0x2C
        public uint gpuIndex;           // 0x30 GPU transport only
        public uint gpuGeneration;      // 0x34 GPU transport only
    }
}
