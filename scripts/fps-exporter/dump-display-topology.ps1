<#
.SYNOPSIS
  Map PresentMon's numeric VidPnSourceId (the "output" label on game_fps/display_fps/
  frames_dropped_total) to each monitor's real connector type (HDMI/DisplayPort/DVI/...)
  and friendly name, via the Windows QueryDisplayConfig API.

.DESCRIPTION
  MUST be run from your normal interactive desktop session — not elevated, not over RDP,
  not from a Windows Service. QueryDisplayConfig reports the topology of whichever session
  calls it; the fps-exporter service runs in Session 0 (non-interactive, no real monitors
  attached in the WDDM sense), and an RDP session sees RDP's own virtual "Remote Display
  Adapter" instead of your physical monitors — confirmed empirically earlier the same day
  this script was written, via a completely different tool (EnumDisplaySettings) hitting the
  exact same problem. Run this at your own keyboard, on the physical console session.

  Writes connector-map.json next to this script, e.g.:
    {"0": "DisplayPort", "1": "HDMI"}
  fps-exporter.js reads that file once at startup (see loadConnectorMap() in fps-exporter.js)
  and attaches a connector="..." label wherever the output ID matches. Missing or stale is
  harmless — panels just fall back to the numeric output ID with no connector label.

  UNTESTED IN THIS REPO'S DEV ENVIRONMENT — the struct layouts below are hand-written from
  the documented Win32 DisplayConfig API (stable, unchanged in the SDK for years), but no
  execution of this exact script has been verified end-to-end. Sanity-check the output:
  cross-reference monitorFriendlyDeviceName in the console output against Settings > System
  > Display > Advanced display > "Display information" for each monitor. If a friendly name
  comes back garbled or empty, don't trust that entry's connector type either — the win32
  call may have partially failed for that target even though it returned SUCCESS overall.

.EXAMPLE
  .\scripts\fps-exporter\dump-display-topology.ps1
#>

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public class DisplayTopology {
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_MODE_INFO {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] modeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    const int ERROR_SUCCESS = 0;

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    public static string TechName(uint tech) {
        switch (tech) {
            case 0: return "VGA";
            case 1: return "SVideo";
            case 2: return "CompositeVideo";
            case 3: return "ComponentVideo";
            case 4: return "DVI";
            case 5: return "HDMI";
            case 6: return "LVDS";
            case 8: return "D_JPN";
            case 9: return "SDI";
            case 10: return "DisplayPort";
            case 11: return "DisplayPort (embedded)";
            case 12: return "UDI";
            case 13: return "UDI (embedded)";
            case 14: return "SDTVDongle";
            case 15: return "Miracast";
            case 16: return "IndirectWired";
            case 17: return "IndirectVirtual";
            case 0x80000000: return "Internal";
            default: return "Unknown(" + tech + ")";
        }
    }

    public static string Run() {
        uint pathCount, modeCount;
        int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
        if (rc != ERROR_SUCCESS) return "ERROR: GetDisplayConfigBufferSizes failed, code " + rc;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (rc != ERROR_SUCCESS) return "ERROR: QueryDisplayConfig failed, code " + rc;

        var sb = new System.Text.StringBuilder();
        for (uint i = 0; i < pathCount; i++) {
            var p = paths[i];
            var nameReq = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            nameReq.header.type = (uint)DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            nameReq.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
            nameReq.header.adapterId = p.targetInfo.adapterId;
            nameReq.header.id = p.targetInfo.id;
            string friendlyName = "<name lookup failed>";
            if (DisplayConfigGetDeviceInfo(ref nameReq) == ERROR_SUCCESS) {
                friendlyName = string.IsNullOrEmpty(nameReq.monitorFriendlyDeviceName) ? "<empty>" : nameReq.monitorFriendlyDeviceName;
            }
            sb.AppendLine(p.sourceInfo.id + "\t" + TechName(p.targetInfo.outputTechnology) + "\t" + friendlyName);
        }
        return sb.ToString();
    }
}
"@

Write-Output "=== Raw topology: sourceId <tab> connector <tab> friendly name ==="
Write-Output "Sanity-check the friendly names against Settings > System > Display > Advanced display before trusting the map below."
Write-Output ""
$raw = [DisplayTopology]::Run()
if ($raw -like "ERROR:*") {
    Write-Error $raw
    exit 1
}
Write-Output $raw

$map = @{}
$rows = $raw -split "`n" | Where-Object { $_.Trim() -ne "" }
foreach ($row in $rows) {
    $parts = $row -split "`t"
    if ($parts.Count -ge 2) {
        $map[$parts[0].Trim()] = $parts[1].Trim()
    }
}

$outPath = Join-Path $PSScriptRoot "connector-map.json"
$map | ConvertTo-Json | Set-Content -Path $outPath -Encoding utf8
Write-Output ""
Write-Output "Wrote $outPath :"
Get-Content $outPath
Write-Output ""
Write-Output "Restart the FpsExporter service (or just fps-exporter.js if running manually) to pick this up — it's only read at startup."
