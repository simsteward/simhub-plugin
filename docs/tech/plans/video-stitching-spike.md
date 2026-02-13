# Tech Plan: Video Stitching Spike

**Related FR-IDs:** FR-013  
**Related Story:** `docs/product/stories/FR-013-Video-Stitching.md`  
**Risk Level:** Medium (PRD Constraint #2)  
**Last updated:** 2026-02-13

---

## Question to Answer

How should the plugin combine multiple camera-angle clips (from FR-012) into a single sequential video file with minimal quality loss and acceptable dependency/setup burden?

---

## Options Evaluated

### Option A: FFmpeg CLI

- **Approach:** Invoke FFmpeg as a process (e.g., `Process.Start`) with concat demuxer: same-codec files listed in a temp file, `ffmpeg -f concat -i list.txt -c copy output.mp4`. No re-encoding if codecs match.
- **Pros:** Well-documented, no re-encoding, supports MP4/MKV. Full control over output path and naming.
- **Cons:** Requires FFmpeg on the user's machine or bundled with the plugin. Bundling increases plugin size; PATH dependency can confuse users.
- **Spike tasks:** (1) Verify `-f concat -c copy` works with OBS-generated MP4/MKV. (2) Test process invocation from .NET 4.8 (working directory, argument escaping, async wait). (3) Decide: bundle a minimal FFmpeg build vs. detect FFmpeg on PATH and document installation.

### Option B: OBS Scene Switching (Single Continuous Recording)

- **Approach:** Instead of separate recordings per camera, switch the in-game camera during one continuous OBS recording. One file; no post-processing.
- **Pros:** No FFmpeg, no stitching step. Simpler for the user.
- **Cons:** Different product behavior — one clip with camera cuts at fixed times, not "angle 1 then angle 2" as separate segments. May not match the "sequential angles" requirement in FR-013. Also requires precise timing of camera switches during the same replay playback.
- **Verdict:** Alternative product design. If we want "angle 1 full clip, then angle 2 full clip" in one file, Option B does not satisfy that; use Option A or C.

### Option C: Ship Separate Files First, Stitching Later

- **Approach:** FR-012 delivers one file per angle. No stitching in initial Part 2 release. User manually concatenates or uploads multiple files. Add stitching in a fast-follow.
- **Pros:** Unblocks Part 2; no FFmpeg or process dependency. Low risk.
- **Cons:** FR-013 is marked Must (Part 2). If we defer, we either downgrade FR-013 to a later release or ship Part 2 without "single output file" until the spike is resolved.
- **Verdict:** Viable if we accept "Part 2 = multi-camera recording only, single file in Part 2.1" or if we choose Option A and implement it.

---

## Recommendation

- **Primary:** Option A (FFmpeg CLI). Implement with: (1) Detect FFmpeg on PATH; (2) Optional: bundle a minimal static FFmpeg build (e.g., ffmpeg.exe in plugin folder) and use it if PATH is empty. Document in user guide. Concat demuxer keeps quality and is simple.
- **Fallback:** Option C for MVP Part 2 if Option A proves brittle (e.g., codec mismatches across OBS versions). Then add stitching in a follow-up with a clear spike result.

---

## Spike Test Plan (Option A)

1. **Concat test:** Produce two MP4 clips (e.g., from OBS or test files). Create a concat list file. Run `ffmpeg -f concat -i list.txt -c copy out.mp4`. Verify output plays correctly and duration is sum of inputs.
2. **Process from C#:** From a .NET 4.8 test app or SimHub plugin, start FFmpeg process with arguments, wait for exit, check exit code. Verify output file exists. Test with paths containing spaces.
3. **OBS output:** Use OBS to record two short clips (same settings). Run concat. Confirm no re-encoding and no errors (same codec/format).
4. **Async/non-blocking:** Run FFmpeg in a background task so the UI does not freeze. Report completion via event or property (for FR-013 "notify when stitching is complete").

---

## Success Criteria

- **GREEN:** FFmpeg concat works reliably with OBS outputs; we can invoke it from the plugin without blocking; we have a strategy for FFmpeg availability (PATH or bundled).
- **YELLOW:** Works but only with specific OBS formats or after user installs FFmpeg; document clearly.
- **RED:** Concat fails (e.g., codec issues) or process invocation is problematic; fall back to Option C and defer FR-013.

---

## Output for FR-013 Spec

After spike: document in this file or in FR-013 spec:

- Chosen approach (A, B, or C).
- Exact FFmpeg arguments (if A).
- Where to get FFmpeg (PATH vs. bundled path).
- Error handling: FFmpeg not found, FFmpeg fails, output file missing.
