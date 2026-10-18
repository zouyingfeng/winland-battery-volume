# Bluetooth battery data layer — test results on this machine

**Status: PASS.** A pure CfgMgr32 P/Invoke reader (`reader-prototype.cs`) returns exactly the two
connected headsets with the correct battery percentages, clean names, stable unique ids and a
connected flag. Measured warm read latency: **~2 ms** (vs ~5.9 s for the equivalent
`Get-PnpDevice` + `Get-PnpDeviceProperty` PowerShell loop). No admin rights, no WinRT, no NuGet,
no CIM/WMI.

- Reader: [`reader-prototype.cs`](reader-prototype.cs) — copy-paste-ready, C# 5 → .NET 10 compatible
- Self-test harness: [`reader-prototype.selftest.cs`](reader-prototype.selftest.cs) + [`scratch/selftest.csproj`](scratch/selftest.csproj)
- Limitations & source comparison: [`limitations.md`](limitations.md)

## 1. Test environment (measured 2026-10-18, Asia/Shanghai)

| Fact | Value |
|---|---|
| OS | Windows 10 Pro for Workstations (registry `ProductName`), DisplayVersion **26H2**, build **26300.9457** (`.NET` reports `10.0.26300.0`), x64 |
| CPU | AMD Ryzen 5 5600 6-Core |
| Shell | PowerShell **5.1.26100.9444** (elevated, `High` integrity) |
| .NET SDK | **10.0.402** at `C:\Users\Administrator\AppData\Local\Microsoft\dotnet\dotnet.exe`; runtimes 10.0.13 (NETCore/WindowsDesktop/AspNetCore) |
| Bluetooth radio | `Intel(R) Wireless Bluetooth(R)`, `USB\VID_8087&PID_0029\6&28CF390B&0&13`, Status OK |
| BT services | `bthserv`, `BTAGService`, `BthAvctpSvc` — all Running |
| Paired BT devices | exactly 2 (`HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices`: `5259200080af`, `220509090778`) |
| Connected headsets | **HECATE G4 S PRO** (`52:59:20:00:80:AF`), **HECATE GT4 S** (`22:05:09:09:07:78`) — classic BR/EDR over HFP |

Both headsets were connected for the whole session; no hardware state was changed (no
disconnect/re-pair/adapter toggle — see §6).

## 2. Reader API (what the plugin consumes)

```csharp
using WinIsland.BluetoothBattery;

// Any thread, never throws, never returns null. Empty list == no connected Bluetooth
// battery-reporting device (no adapter / nothing connected / nothing reports battery).
List<BluetoothBatteryDevice> devices = BluetoothBatteryReader.Read();
// optional: BluetoothBatteryReader.Read(Action<string> log) to forward non-fatal issues to plugin log

foreach (var d in devices)
{
    // d.Id            "bt:5259200080AF"            stable diff key
    // d.Address       "52:59:20:00:80:AF"          formatted Bluetooth address ("" if unknown)
    // d.Name          "HECATE G4 S PRO"            clean name (service suffixes already stripped)
    // d.Percent       50                          0..100
    // d.IsConnected   true                        present + started + no problem code
    // d.IsPresent     true
    // d.Transport     "Classic" | "BLE" | "Unknown"
    // d.ContainerId   "{86CAF97E-...}"             PnP container GUID
    // d.InstanceId    "BTHENUM\{0000111E-...}..." battery-bearing service node (diagnostics)
}
```

Recommended plugin usage: poll every **2–5 s** from `Context.CreateTimer(...)`; ~2 ms per poll.
Diff on `Id` to avoid re-rendering when nothing changed.

## 3. Observed device list — exact reader output

Command (built and run as shown in §7):

```
cold read: 2 device(s) in 20.54 ms
instance ids / percent / connected / transport / container:
  [0] HECATE G4 S PRO | address=52:59:20:00:80:AF | percent=50 | connected=True | present=True | transport=Classic | container={86CAF97E-FBB8-5A0E-B6A4-D08E17D6F8E7}
      id=bt:5259200080AF
      node=BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0001005d_PID&223b\8&3aef26f0&0&5259200080AF_C00000000
  [1] HECATE GT4 S | address=22:05:09:09:07:78 | percent=80 | connected=True | present=True | transport=Classic | container={62E96E6E-DD10-584D-A575-FD7FDA708C8D}
      id=bt:220509090778
      node=BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&000107e3_PID&2002\8&3aef26f0&0&220509090778_C00000000
[PASS] expect 5259200080AF=50 (name 'HECATE G4 S PRO')
[PASS] expect 220509090778=80 (name 'HECATE GT4 S')
```

The battery property lives on the **HFP AudioGateway service node** (`BTHENUM\{0000111E-...}`,
Class `System`, friendly name `"... Hands-Free AG"`), not on the A2DP/Avrcp/DEV nodes. A broad
scan of all **262 present** PnP devices found the battery property on exactly these **2** nodes and
nowhere else.

Raw property dump of the two nodes (`Get-PnpDeviceProperty`, probe `scratch/probe1.ps1`):

| Battery | Type | FriendlyName | DeviceDesc | BusReportedDeviceDesc | Status flags | Problem | ContainerId |
|---|---|---|---|---|---|---|---|
| 50 | Byte | HECATE G4 S PRO Hands-Free AG | Microsoft Bluetooth 配置文件 AudioGateway 角色 | 蓝牙外围设备 (`Hands-free unit`) | `0x0180600A` | 0 | `{86CAF97E-FBB8-5A0E-B6A4-D08E17D6F8E7}` |
| 80 | Byte | HECATE GT4 S Hands-Free AG | (same) | (same) | `0x0180600A` | 0 | `{62E96E6E-DD10-584D-A575-FD7FDA708C8D}` |

`0x0180600A` = `DN_DRIVER_LOADED | DN_STARTED | DN_DISABLEABLE | DN_REMOVABLE | DN_NT_ENUMERATOR | DN_NT_DRIVER`.

## 4. Verification against `Get-PnpDeviceProperty` (the Lead's baseline)

`scratch/compare.ps1` runs the reader, then independently scans all present PnP devices with
`Get-PnpDevice -PresentOnly` / `Get-PnpDeviceProperty` and compares per device. Output
([`scratch/compare-output.txt`](scratch/compare-output.txt)):

```
PowerShell baseline: present=262 nodes; battery nodes=2
  baseline node: class=System friendly='HECATE G4 S PRO Hands-Free AG' battery=50
                BTHENUM\{0000111E-...}_VID&0001005D_PID&223B\8&3AEF26F0&0&5259200080AF_C00000000
  baseline node: class=System friendly='HECATE GT4 S Hands-Free AG' battery=80
                BTHENUM\{0000111E-...}_VID&000107E3_PID&2002\8&3AEF26F0&0&220509090778_C00000000

[PASS] HECATE G4 S PRO: reader 50% == Get-PnpDeviceProperty 50%  (node 5259200080AF)
[PASS] HECATE G4 S PRO: clean name == friendly name of BTHENUM\DEV_5259200080AF\8&3AEF26F0&0&BLUETOOTHDEVICE_5259200080AF
[PASS] HECATE GT4 S: reader 80% == Get-PnpDeviceProperty 80%  (node 220509090778)
[PASS] HECATE GT4 S: clean name == friendly name of BTHENUM\DEV_220509090778\8&3AEF26F0&0&BLUETOOTHDEVICE_220509090778

CROSS-CHECK RESULT: PASS
```

**No-admin cross-check:** the same self-test was re-run under a reduced token
(`runas /trustlevel:0x20000`: Administrators group deny-only, basic-user trust level) via
[`scratch/run-restricted.cmd`](scratch/run-restricted.cmd): **RESULT: PASS**, same 50 % / 80 %.
Read-only CfgMgr32 property reads need no elevation (Windows Settings reads the same value
non-elevated). Note: `DOTNET_ROOT` had to be set for that test only because the .NET runtime on
this machine lives in a per-user path — irrelevant inside the WinIsland plugin process.

## 5. Name normalization — the rule that actually works

The battery node's own name is a **service name** and must not be shown:

| Node (same physical device) | FriendlyName | usable? |
|---|---|---|
| `BTHENUM\{0000111E-...}\...\8&...&0&5259200080AF_C00000000` (battery) | `HECATE G4 S PRO Hands-Free AG` | ❌ service suffix |
| `BTHENUM\DEV_5259200080AF\8&...&BLUETOOTHDEVICE_5259200080AF` (device container) | `HECATE G4 S PRO` | ✅ |
| `BTHENUM\{0000110B-...}` A2DP MEDIA node | `HECATE G4 S PRO` | ✅ but no battery |
| `BTHENUM\{0000110C-...}` Avrcp | `HECATE G4 S PRO Avrcp 传输` | ❌ service suffix |
| `BTHHFENUM\BTHHFPAUDIO\...` HFP audio | `HECATE G4 S PRO Hands-Free` | ❌ service suffix |

Both nodes of one device share the same **12-hex-digit Bluetooth address** *and* the same PnP
**container id** (`{86CAF97E-...}` / `{62E96E6E-...}`) — verified by direct property dumps.

Reader resolution order (first hit wins), implemented and tested:

1. **L1 (primary, used on this machine):** join battery node → present `BTHENUM\DEV_<MAC>` /
   `BTHLE\DEV_<MAC>` node by the 12-hex address, use its `FriendlyName`. Produces
   `HECATE G4 S PRO` / `HECATE GT4 S` — exactly the names in Windows Settings.
2. **L2:** same container-id sibling node (works if the DEV-node id shape ever changes).
3. **L3:** battery node's own `FriendlyName` + strip known service suffixes — unit-tested:
   `HECATE G4 S PRO Hands-Free AG` → `HECATE G4 S PRO`;
   `HECATE GT4 S Avrcp 传输` → `HECATE GT4 S`;
   `HECATE G4 S PRO Hands-Free` → `HECATE G4 S PRO`.
   Suffix list: `Hands-Free AG`, `Hands-Free`, `Avrcp 传输`, `Avrcp Transport`, `Avrcp`,
   `A2DP Sink`, `A2DP`, `Audio Gateway`, `HFP AG`, `HFP`.
4. **L4:** formatted MAC address (still unique/stable; never shows a service-node name).

**Rule for the Lead:** show the `BTHENUM\DEV_<MAC>` friendly name (reader's `.Name`); if the plugin
ever falls back to raw PnP data, strip one of the suffixes above. Do *not* display
"... Hands-Free AG" — Windows Settings doesn't either.

## 6. Connected / present-state semantics (what was observed)

- Enumeration with `CM_GETIDLIST_FILTER_PRESENT` returned **262 present** devnodes; enumerating
  without the flag returned **285** devnodes; `CM_Locate_DevNodeW(..., flags=0 = NORMAL)` succeeded
  for 262, `flags=1 = PHANTOM` for 285 → presence = `locate(NORMAL) == CR_SUCCESS`.
- All **21** `BTH*` devnodes are present; **0 phantom/non-present Bluetooth nodes** exist right now
  (all 285−262=23 non-present nodes are non-Bluetooth).
- Devnode status of both battery nodes: `0x0180600A`, problem code `0` → started, healthy.
  Reader's `IsConnected = present && (status & DN_STARTED) && !(status & DN_HAS_PROBLEM)
  && !(status & DN_DEVICE_DISCONNECTED) && problem == 0`. Both devices report `True`.
- The battery value is **not persisted** in the registry Enum store: a recursive search of
  `HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM` for the property-set GUID `{104EA319-...}` found
  **0 hits** (15.6 ms). The AG node's Enum key contains only `Device Parameters\WDF`. So there is
  no offline/cached registry copy to read once the node is gone.
- **Not-connected state was deliberately not triggered** (would interrupt the user's audio;
  the brief said not to). What the code does when a headset disconnects: its `BTHENUM\{...}` HFP
  service node disappears from the present list → the reader skips it → the device vanishes from
  the returned list. There is no "last known percentage" in the PnP store. If the OS ever leaves a
  phantom node with a cached battery value, the node would appear (only when enumerating without
  the PRESENT flag — not what the reader does) and `IsConnected` would be `False`.
- **Zero-device case — tested without hardware:** a test variant of the reader with the Bluetooth
  fast path disabled forced the "no battery found" path; it scanned the remaining 241 present nodes,
  returned an **empty, non-null list** and logged
  `cfg: no battery on Bluetooth nodes; running broad fallback over 241 other present nodes` /
  `cfg: scanned 241 present node(s), 0 battery device(s): none` — no exception. Warm 7.59 ms,
  cold 225 ms (.NET Framework/csc build). See [`scratch/empty-path-smoke.cs`](scratch/empty-path-smoke.cs).

**Plugin empty state recommendation:** when `Read()` returns 0 devices, remove the island content
(`Context.Island.SetContent(null)`) so the island stays idle — the plugin should not display a
placeholder 0 %/ghost device. A settings page may show "No Bluetooth devices connected". This
matches Windows Settings, which only shows battery for connected devices.

## 7. Latency measurements

### CfgMgr32 reader (in-process, .NET 10 Release)

| Run | Cold (first call, incl. JIT) | Warm min / avg / max | Notes |
|---|---|---|---|
| n=20 | 19.27 ms | 1.64 / 2.09 / 3.02 ms | 2 devices |
| n=30 | 20.54 ms | 1.72 / 2.32 / 3.05 ms | 2 devices |
| n=50 | — | **1.57 / 1.95 / 2.88 ms** | 2 devices, stable across all 50 reads |
| empty path (241-node broad scan) | 225.41 ms (.NET Framework csc build) | 7.59 ms | 0 devices, no throw |
| concurrency | — | 4 threads × 10 reads, 0 failures | stateless/read-only |

The ~20 ms cold number is one-time JIT + first CfgMgr32 call; steady-state polling costs ~2 ms.

### PowerShell equivalent (`Get-PnpDevice` / `Get-PnpDeviceProperty`)

| Operation | Cold | Warm |
|---|---|---|
| `Get-PnpDevice -PresentOnly` (262 devices) | 1019–1315 ms | 295–322 ms |
| loop `Get-PnpDeviceProperty` over **all 262** present nodes | — | **74,618–81,436 ms** |
| loop `Get-PnpDeviceProperty` over **21 `BTH*`** nodes (targeted) | — | **5,948 ms** |

So the P/Invoke reader is ~**3,000×** faster than a targeted PowerShell/CIM scan and ~**38,000×**
faster than a full scan. It also avoids a real `Get-PnpDeviceProperty` reliability problem observed
here: in the 262-node loop the second battery node intermittently returned blank
FriendlyName/ContainerId/Parent (battery value still present) even though a direct query right after
returned all values — reproduced twice. CfgMgr32 direct calls were consistent across 50+ reads and
4 concurrent threads.

## 8. CfgMgr32 flag semantics (probed, `--diag` output)

`CM_Get_Device_ID_List_SizeW/W(NULL, flags)` and enumerator-filtered variants
(filter strings only apply together with the ENUMERATOR flag):

| filter | flags | count | meaning |
|---|---|---|---|
| `NULL` | `0x000` | 285 | **all** devnodes (incl. 23 non-present) |
| `NULL` | `0x100` | 262 | **present** devnodes ← what the reader uses |
| `NULL` | `0x001` | error `cr=0x3` (CR_INVALID_FLAG) | ENUMERATOR flag needs a filter |
| `BTHENUM` | `0x001` | 15 | enumerator-filtered (all BTHENUM nodes) |
| `BTHENUM` | `0x101` | 15 | present BTHENUM nodes |
| `BTH` | `0x001` / `0x101` | 4 | BT enumerator root nodes (`BTH\MS_*`) |
| `BTHLE` | `0x001` / `0x101` | 0 | no BLE devices on this machine |

Conclusions used in the reader: `CM_GETIDLIST_FILTER_PRESENT = 0x00000100`,
`CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001`, `CM_LOCATE_DEVNODE_NORMAL = 0`,
`CM_LOCATE_DEVNODE_PHANTOM = 1`. (With a `NULL` filter, passing any other flag is an error, so the
reader enumerates all-present then filters instance ids by the `BTH` prefix client-side — every
Bluetooth enumerator is `BTHENUM`/`BTHLE`/`BTHHFENUM`/`BTHLEDevice`/`BTH\...`, and the
`DEVPKEY_Bluetooth_*` property set only exists on Bluetooth nodes.)

## 9. Fallback compile paths (in case the .NET 10 SDK is not used)

The reader is written in C# 5-compatible syntax and compiles both ways; both were exercised:

1. **`csc.exe` .NET Framework 4.0 compiler** (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`):
   compiled `reader-prototype.cs` + `scratch/csharp5-smoke.cs`, ran → `C#5 build OK, devices=2`,
   `HECATE G4 S PRO | 52:59:20:00:80:AF | 50% | connected=True`.
2. **PowerShell 5.1 `Add-Type -TypeDefinition`** (CodeDOM): compiled in 124 ms, then
   `[WinIsland.BluetoothBattery.BluetoothBatteryReader]::Read()` returned the same 2 devices/values.

So the plugin can also be prototyped entirely from PowerShell 5.1 if needed — no .NET SDK required
(the final plugin still targets `net10.0-windows10.0.26100.0` like WinIsland).

## 10. Exact commands used

```powershell
# build + run the self-test (dotnet 10 SDK)
$dotnet = 'C:\Users\Administrator\AppData\Local\Microsoft\dotnet\dotnet.exe'
& $dotnet build .\_research\bt-proto\scratch\selftest.csproj -c Release
& $dotnet .\_research\bt-proto\scratch\bin\Release\net10.0\BtBatterySelfTest.dll `
    --diag --bench 50 --expect 5259200080AF=50 --expect 220509090778=80 --expect-count 2

# cross-check against Get-PnpDeviceProperty + latency comparison
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\_research\bt-proto\scratch\compare.ps1

# C# 5 / .NET Framework fallback
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe `
    /out:scratch\csharp5-smoke.exe _research\bt-proto\reader-prototype.cs scratch\csharp5-smoke.cs

# no-battery / zero-device path
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe `
    /out:scratch\empty-path-smoke.exe scratch\reader-broadonly.cs scratch\empty-path-smoke.cs
& .\_research\bt-proto\scratch\empty-path-smoke.exe

# non-admin proof
runas /trustlevel:0x20000 "cmd.exe /c ""C:\...\_research\bt-proto\scratch\run-restricted.cmd"""
```

Evidence files kept under [`scratch/`](scratch/): `compare-output.txt`, `probe1-output.txt`,
`restricted-run.txt`, plus the probe scripts (`probe1.ps1`, `probe-flags.ps1`, `probe-flags2.ps1`),
build harness (`selftest.csproj`, `run-restricted.cmd`), and fallback smoke tests.

## 11. Caveats (details in [`limitations.md`](limitations.md))

1. Values are the **system cache of the last value the device reported** — not a live gauge. They
   update when the headset pushes a new value (typically on connect/change), not on a schedule.
2. Only **connected** devices are reported; a paired-but-disconnected headset has no service node
   and therefore no battery entry (no offline cache — verified). Plan the UI empty state accordingly.
3. The property exists for classic HFP headsets (tested) and is expected for BLE devices whose
   battery the Windows stack exposes (via `BTHLE\...` nodes) — **no BLE device was attached to this
   machine, so the BLE path is coded but untested**.
4. Devices whose battery never reaches Windows (some TWS earbuds use vendor-only BLE services) will
   simply not appear — same as Windows Settings. The plugin must not fabricate a value.
5. `IsConnected` is a guard (present+started+problem-free), not a replacement filter: the reader only
   ever enumerates present nodes, so entries are connected in practice.
6. Name fallback L3 strips known localized service suffixes (`Avrcp 传输` is the zh-CN form observed);
   primary L1 path does not need it on this machine.
