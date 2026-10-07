# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A fork of Eosin's VRRenderer plugin for **Virt-A-Mate** (VaM) — a Unity-based 3D character app. It records stereo/monoscopic 360° and 180° video from inside VaM, exports BVH animation, records audio, and can stream frames over TCP.

Three codebases are merged in one tree:

| Origin | Lives in | Marker |
|---|---|---|
| Eosin's VRRenderer (v15) | `src/Eosin_VRRenderer.cs` | English comments, `VRRenderer: ` log prefix |
| yunidatsu's multi-threaded encoder + TCP streaming | merged into the above | credited at `Eosin_VRRenderer.cs:12` |
| This fork's MMDShow / mmd2timeline Player integration | `src/Eosin_VRRenderer.Ext.cs` | **Chinese** comments, `noone77521` namespace, `【mmd2timeline】` log prefix |

`namespace noone77521`, `Config.saveDataPath`, and the `Capturer::` log source string are mmd2timeline leftovers, not Eosin's. Don't "fix" them as typos.

## Building

**This project cannot be built in this checkout.** Two independent blockers, both verified:

- `Eosin.csproj` resolves all ~60 Unity references through a hardcoded relative path, `..\..\..\..\..\vam\VaM_Data\Managed\`, which from this directory points at `K:\vam\VaM_Data\Managed` — VaM is not installed here.
- No MSBuild is installed (`msbuild`/`csc`/`mcs` absent; only `dotnet` 9 SDK, which won't build a non-SDK `.NET Framework 3.5` csproj). The v3.5 targeting pack *is* present.

On a machine with VaM installed such that that path resolves:

```bash
msbuild Eosin.csproj /p:Configuration=Release     # -> bin/Release/Render.dll
```

`AssemblyName` is `Render`, not `Eosin` — output is `Render.dll`. There is no test suite, no linter config, and no CI.

Two hard rules when adding or moving a `.cs` file:

1. Add it to the `<Compile Include>` list in `Eosin.csproj` (~line 302).
2. Add it to `Render.cslist`, **with Windows backslashes**.

`Render.cslist` is the VaM multi-file manifest, not a shader list — VaM compiles its entries into the same assembly as the entry `.cs`. That is why `Eosin_VRRenderer.cs`, `.Ext.cs`, and `.UI.Ext.cs` are three `partial class VRRenderer` files, and why `MacGruber.Utils` is in the same compilation unit with no DLL reference. The entry file `Render.cs` is **not in this repo**.

## Architecture

### The render mode × stereo mode cross product

`UpdateRenderMode()` (`Eosin_VRRenderer.cs:1515`) turns `renderModeIdx` (6 values) and `stereoModeIdx` (4 values) into booleans. Nearly every `if` in the 4,334-line main file is one of these two axes — this is why the file reads as repetitive:

```
renderModeIdx  0 Flat  1 VR180 Stereo  2 VR360 Stereo  3 VR180 Mono  4 VR360 Mono  5 BVH
stereoModeIdx  0 Static(cubemap)  1 Panoramic  2 Triangle  3 Square
```

Mode 5 (BVH) is a **completely separate pipeline** that bypasses the render/encode machinery entirely. There are no feature-gating preprocessor symbols — the only `#if` in the whole repo is `#if DEBUG` in `LogUtil.cs`.

### The heartbeat is `Camera.onPreRender`, not `Update()`

`Init()` subscribes `Camera.onPreRender += OnPreRenderCallback` (`:619`). Because it fires on any camera's pre-render, it piggybacks on VaM's own render tick. `Update()` only re-arms the guard (`handledPre = false`, `:1849`).

```
RecordVideo()  :2671  →  BeginRender()  :3445   (alloc RTs, thread pool, TcpClient)
                        ↓
   OnPreRenderCallback  :2264   ── if (bRendering && bBvhRender) → UpdateBVHRender(); return
                        ↓
     UpdateRenderCamera  :2377  →  PrepareFrame  :2486   (video-seek handshake, framesToSkip)
        ├── acquire encoder slot from Semaphore  :2380
        ├── RenderFrame(threadIdx)  :2561   (all GPU work: cubemap/tri/square/panoramic/flat)
        └── ProcessFrame(threadIdx)  :2413   → RenderTexToTex2D → SaveRenderAsFile (queues encode)
                        ↓
   EndRender()  :3705   (drains the semaphore — blocks the Unity frame loop; destroys everything)
```

Escape aborts mid-recording (`EndRender()`, `:1908`).

### Threading is ThreadPool + Semaphore, and it's only about encoding

The off-thread work is **encode + file write, nothing else**. Capture itself is synchronous `Camera.Render()` + `Texture2D.ReadPixels` (no `AsyncGPUReadback`) on the main thread. That's the point: per the header comment, JPEG encoding is ~70% of frame time and PNG ~90% at 8k.

- `numEncThreads` default 4, max `MAX_ENC_THREADS` = 16. **`0` selects a single-threaded path** (null semaphore, inline save).
- **TCP streaming forces `numEncThreads = 1`** — the socket is not thread-safe.
- Each worker owns one `Texture2D` for its whole lifetime (`finalOutputTextures[]`); no per-frame CPU buffer allocation.
- `encThreadFreeFlags` is a plain `bool[]` written from worker threads with no `volatile`/memory barrier. It works in practice because the `Semaphore` release/wait pair supplies the edge, but don't rely on that if you touch it.

### Four VR projection paths

All funnel through `RenderFrame()` (`:2561`):

- **Cubemap→equirect** (`RenderCubemap` :4079) — the default. 6 faces at 90°, `lilyRenderOverlap` widens FOV for smooth stitching. Skips the back face in 180° mode.
- **Triangular** (`:4021`) — 3×3 cameras, 3 seams.
- **Square** (`:4048`) — 4×3 cameras, 4 seams; top/bottom rows rendered at half resolution.
- **Panoramic pixel-slice** (`RenderPanoramicStereoMap` :3913) — renders one *pixel column* at a time. Thousands of `cam.Render()` calls per eye; 15–30× slower. Allocates and destroys 5 GPU objects **every frame**.

### Transparency doubles the whole render

kuler's method: the predicate below is repeated verbatim in ~8 places:

```csharp
(myFileFormat == FORMAT_PNG && preserveTransparencyChooser.val) ||
(renderBackgroundChooser.val && (previewBackground != null || videoPlayer != null))
```

When true, every view is rendered **twice** (against black and against white) and `AlphaFromDifferenceShader()` recovers alpha from the difference. Budget VRAM accordingly (`GetMemoryEstimate()` :1430). If you add a new render path, you almost certainly need the `blackBgRenderTex`/`whiteBgRenderTex` pair too.

### Localization is a filename-collision trap

English UI strings *are* the persistence keys. `SetupToggle("Preview", ...)` passes `"Preview"` as the `JSONStorable` key and `Lang.Get("Preview")` as the display label. **Renaming a setup key silently orphans every user's saved settings.**

- The seam is `src/Eosin_VRRenderer.UI.Ext.cs` — the only place `paramName` and `label` are separated. Any new UI control built without it is untranslatable.
- `Lang.Get()` returns the key itself when missing, and self-registers it. So untranslated strings degrade gracefully to English.
- Resolution order: hardcoded `LoadChinese()` → `<pluginPath>/Lang/<SystemLanguage>.json` → user's `Saves/PluginData/mmd2timeline/lang.json` override.
- `Lang.GenerateProfile()` dumps all keys for translation and opens Explorer — but **nothing calls it** in this fork. It's the intended extraction workflow, currently unwired.

### Cross-plugin coupling fails silently

The MMDShow bridge talks to mmd2timeline Player through VaM's `JSONStorable` API — no sockets, no compiled reference, pure duck-typing on **English parameter-name strings** (`"Ready To Render"`, `"Current FOV"`, `"Camera Control"`, …). `RefreshPlayerPluginList()` (Ext.cs:794) scans atoms for `mmd2timeline.Player` and feature-detects by param presence; older Player builds without render support are silently excluded.

Every getter returns a hardcoded default on miss, so **a version mismatch degrades quietly instead of erroring.** If you rename a parameter, this is what breaks.

Handshake: `BeginRender()` → `ReadyToPlayerRender()` (saves & overwrites the other plugin's camera settings) → Player reports ready → main `Update()` calls `StartPlayerRender()` → `EndRender()` calls `EndPlayerRender()` (restores all five settings).

### Gotchas that will bite

- **`frameRateInt` and `secondsToRecord` are `static`, not const** (`:422`, `:424`). Two atoms running the plugin share them — one atom's slider silently retimes the other.
- **Resource cleanup is manual and asymmetric.** `OnDestroy()` does *not* call `EndRender()`; removing the plugin mid-recording leaks the thread pool, render targets, `TcpClient`, and `Allocator.Persistent` audio buffers. `EndRender()` `:3782` dereferences `renderCamParentObj` with no null check.
- **Two log prefixes from one plugin.** The main file uses raw `SuperController.LogMessage("VRRenderer: ...")`; `Ext.cs`/`Lang.cs` use `LogUtil` → `【mmd2timeline】`. Match whichever convention the surrounding file uses.
- **Folder name is load-bearing.** `Utils.GetPluginPath(self)` string-splits `self.name` on the first `_` to derive the plugin id. The class/file name `Eosin_VRRenderer` → `Eosin`, and the same path locates `VRRenderer.shaderbundle` at load time. Renaming the class breaks plugin loading.
- `MVR.FileManagementSecure` handles all file I/O (`FileManagerSecure`), not `System.IO`. Don't "fix" it.
- `SetVRRenderPosition` (`:1166`) moves the user's atom transform to a hardcoded sit/stand position and never moves it back.
- The only MSAA default is `DEFAULT_MSAA_IDX = 3` → **8x MSAA**, a ~9× VRAM multiplier.
- **A storable gets exactly one popup.** `Utils.SetupStringChooser` ends with `CreateScrollablePopup` (`MacGruber_Utils.Ext.cs:91`), so calling `CreateFilterablePopup` on its result returns **null** — the next `.label` throws. `Camera Target` (`:792-797`) is hand-constructed precisely so this doesn't happen. Pick one helper or the other, never both.
- **An exception thrown while building the UI is catastrophic and misattributed.** `BuildExtUI()` used to run near the top of `BuildUI()`, so a throw orphaned every field assigned after it, and `Update()` then threw a `NullReferenceException` every frame — pointing at `Update`, never at the real line, because VaM compiles without debug symbols. `BuildExtUI()` now runs last and is wrapped in a `try`/`catch` that logs `VRRenderer::BuildUI.BuildExtUI:`. Keep it last. If you ever see per-frame NREs from `Update` or `Setup`, suspect a UI-construction failure first.

## Cam Forward by FOV

Non-flat-mode dolly in `Ext.cs` `FixedUpdate()`: the plugin's own atom **tracks the motion source continuously** — both position and rotation are copied every tick, and the position is then displaced along the motion source's forward axis by a FOV-driven delta. Gated on `_syncFovJSON.val && renderModeIdx != 0 && EnablePlayerRender` — so it needs Sync FOV on, a non-Flat/non-BVH mode, **and** a player plugin selected.

`delta = (40 - currentFov) * ratio + offset`, then `SetPositionAndRotation(motionSource.position + motionSource.forward * -delta, motionSource.rotation)`. **FOV 40 is an absolute pivot, not a captured baseline** — below 40 always moves forward, above 40 always backward, and nothing is stashed between frames. Consequence: while this is active the atom cannot be moved or aimed by hand, and with the viewport camera as source the atom's heading follows the camera wherever it looks.

An earlier version measured the delta against a captured `_camForwardBaseFov` but picked the *ratio* by comparing FOV to 40 absolutely. That mixed references and inverted the direction whenever the baseline sat below the current reading (base 25, current 35 → zoom-in ratio, but backward motion). The absolute pivot removed the whole class of bug along with the two state fields guarding it.

- **FOV Source** — viewport camera (`Camera.main.fieldOfView`) or the MMD plugin. Defaults to the MMD plugin. No `setCallbackFunction`, so switching it mid-dolly takes effect the next tick without a jump (the pivot is absolute, so there is no stale state to invalidate).
- **Motion Source** — supplies base position and forward direction, read-only. The viewport camera or any non-Person atom (Person is filtered out of the list; the resolver also rejects it, since a saved value could name one).
- **FOV Source** uses `Utils.SetupStringChooser`; **Motion Source** is hand-constructed and uses `CreateFilterablePopup` like `Camera Target`, because the atom list is long enough to want type-to-filter. Never run both helpers over one storable — see the one-popup rule above.
- **Stored value ≠ display string.** Entries are persisted verbatim, so the viewport option stores as `"Viewport Camera"` while displaying `Main Camera` (`MOTION_SOURCE_VIEWPORT` vs `MOTION_SOURCE_VIEWPORT_LABEL`; same split in `FOV_SOURCE_NAMES` / `FOV_SOURCE_LABELS`). Changing a stored string orphans every save. Never rename them.
- The atom list is a snapshot taken when the UI is built. Adding or removing atoms needs a scene reload — `Camera Target` admits this in its label, this one doesn't.

## Shaders

`shadersource/*.shader`/`.cginc` are sources; `VRRenderer.shaderbundle` and `MacGruber_Convolution.shaderbundle` are **precompiled Unity asset bundles loaded at runtime** (`:605-611`), containing `LilyRender.mat`, `LilyRenderAlpha.mat`, `LilyRenderRotate.mat`, `PixelSliceEquirect.mat`, `AlphaFromDifference.mat`, and `Convolution.mat`.

There is **no shader build tooling in this repo**. Editing `shadersource/` changes nothing until the bundles are rebuilt externally.

## Reading order for a newcomer

1. `UpdateRenderMode()` `:1515` — the two enums that gate everything
2. `OnPreRenderCallback` `:2264` — the actual heartbeat
3. `UpdateRenderCamera` `:2377` → `RenderFrame` `:2561` → `ProcessFrame` `:2413`
4. `SaveRenderAsFile` `:2800` — the threading hand-off
5. `BeginRender` `:3445` / `EndRender` `:3705` — the asymmetry between these is where bugs live
6. `BuildUI` `:675` — ~350 lines of `setCallbackFunction` lambdas; this is the real settings model, the fields at `:403-578` are its shadow
