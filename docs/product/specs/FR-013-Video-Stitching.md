# Spec: Video Stitching (Single Output File)

**FR-IDs:** FR-013  
**Priority:** Must (Part 2)  
**Status:** Ready  
**Part:** 2  
**Source Story:** `docs/product/stories/FR-013-Video-Stitching.md`

---

## Overview

After the multi-camera recording loop (FR-012) produces one clip per angle, this feature combines them into a single sequential video file: angle 1 plays, then angle 2, and so on. The output is one protest-ready file the user can upload to iRacing. Implementation approach depends on the video-stitching spike (`docs/tech/plans/video-stitching-spike.md`).

---

## Detailed Requirements

### R-STCH-01: Input and Order

- **Input:** Ordered list of clip file paths from the last "Record All Angles" run (one per camera, in camera order). Source: FR-012 orchestrator collects `outputPath` from each StopRecord response and passes them to the stitching step (or stores for a "Stitch" action).
- **Order:** Output video plays segments in the same order as the list (angle 1, then angle 2, …).

### R-STCH-02: Output File

- **Location:** User-accessible. Options: (1) Same directory as the first source clip, with a derived name (e.g., `incident_YYYY-MM-DD_HH-mm-ss_stitched.mp4`), or (2) Configurable output directory in settings. Document chosen approach in implementation.
- **Format:** Same as source clips where possible (e.g., MP4). Spike (FFmpeg concat) typically uses `-c copy` so no re-encoding; format follows source.

### R-STCH-03: Quality

- **Prefer no re-encoding:** If the spike recommends FFmpeg concat with `-c copy`, use it so quality is unchanged. If re-encoding is required (e.g., codec mismatch), document and minimize quality loss (e.g., same bitrate/codec as source).

### R-STCH-04: Async and Non-Blocking

- **Execution:** Stitching runs asynchronously (e.g., background task or process). Must not block the SimHub UI or DataUpdate loop.
- **Completion:** When stitching finishes, notify the user: update a SimHub property (e.g., `SimSteward.Stitch.Status` = "Complete" / "Failed") and surface the output path (e.g., `SimSteward.Stitch.OutputPath`). Optionally show a toast or overlay message: "Stitched video saved: {path}."

### R-STCH-05: Formats

- **Supported:** At least MP4 and MKV (common OBS outputs). Exact support depends on spike outcome (FFmpeg concat supports both).

### R-STCH-06: Fallback on Failure

- **If stitching fails:** Do not delete the individual clips. Notify the user (message + status property). User can manually combine or use the clips as separate files.
- **Errors to handle:** FFmpeg not found, process error, disk full, one of the source files missing or locked.

### R-STCH-07: Cleanup of Source Clips

- **Optional:** After successful stitch, offer an option (or setting) to delete the individual angle clips to save space. Default: do not delete; user keeps both stitched and originals unless they opt in.
- **If delete is implemented:** Only delete after stitch completes successfully and user has confirmed (or a setting is enabled). Document in UI.

---

## Technical Design Notes

- **Spike dependency:** Final implementation (FFmpeg vs. other) follows `docs/tech/plans/video-stitching-spike.md`. Spec assumes Option A (FFmpeg concat) unless spike recommends otherwise.
- **Trigger:** Stitching can be triggered automatically when FR-012 loop completes, or by a separate "Stitch" button that uses the last run's clips. Product decision; spec allows either.
- **Process invocation:** If using FFmpeg, create a temp concat list file, invoke FFmpeg with `-f concat -i list.txt -c copy output.mp4`, wait for process exit, then delete temp file. Run in background thread/task.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **FR-012** | Provides the ordered list of clip paths. |
| **Spike** | Defines FFmpeg availability strategy and exact command. |

**Constraint:** Stitching must not block the plugin or SimHub. All work off the main/DataUpdate thread.

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|------------------|
| After multi-camera recording, plugin combines clips into one | R-STCH-01, R-STCH-02 |
| Output plays angle 1 then angle 2... | R-STCH-01 |
| Output saved to user-accessible location | R-STCH-02 |
| Preserve quality (no/minimal re-encoding) | R-STCH-03 |
| User notified when complete, with path | R-STCH-04 |
| Works with MP4/MKV | R-STCH-05 |
| If stitching fails, preserve individual clips and notify | R-STCH-06 |

---

## Open Questions

- Auto-stitch on loop complete vs. explicit "Stitch" action (product choice).
- Optional cleanup of source clips: default off, configurable in settings (recommended).
