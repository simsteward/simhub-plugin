# Video Stitching

**FR-IDs:** FR-013  
**Priority:** Must (Part 2)  
**Status:** Ready  
**Created:** 2026-02-13

## Description

Combine multiple camera angle recordings into a single sequential video file. After the multi-camera recording loop (FR-012) produces one clip per angle, this feature stitches them together: angle 1 plays, then angle 2, etc. The output is a single protest-ready video file the user can upload directly to iRacing.

## Acceptance Criteria

- [ ] After multi-camera recording completes, plugin combines all angle clips into one sequential video
- [ ] Output plays camera angles in order (angle 1 → angle 2 → ...)
- [ ] Output file is saved to a user-accessible location (same directory as source clips or configurable)
- [ ] Stitching preserves video quality (no re-encoding if possible, or minimal quality loss)
- [ ] User is notified when stitching is complete, with the output file path
- [ ] Stitching works with common OBS output formats (MP4, MKV)
- [ ] Fallback: if stitching fails, individual clips are preserved and the user is notified

## Subtasks

- [ ] **Spike: Video stitching approach** -- Evaluate options (PRD Risk #2):
  - Option A: FFmpeg CLI call (bundle or require user to install FFmpeg)
  - Option B: OBS scene switching instead of separate recordings (one continuous recording)
  - Option C: Deliver as separate files initially, add stitching later
- [ ] Implement chosen stitching approach
- [ ] Handle file paths: locate individual clips from the recording loop
- [ ] Run stitching process (async, non-blocking)
- [ ] Show progress/completion notification in UI
- [ ] Clean up individual clip files after successful stitch (optional, configurable)
- [ ] Error handling: stitching failure preserves individual clips
- [ ] Test: stitch 2 MP4 clips, verify output plays correctly

## Dependencies

- FR-012-014-015-Multi-Camera-Clipping (produces the individual camera angle clips)

## Notes

- **This story requires a spike first** (PRD Risk #2). The stitching approach needs evaluation before implementation.
- **FFmpeg concat** is the most straightforward approach. FFmpeg can concatenate same-codec files without re-encoding (`ffmpeg -f concat -i list.txt -c copy output.mp4`). But it requires FFmpeg to be installed or bundled.
- **OBS scene switching** is an alternative: instead of separate recordings, switch cameras within a single OBS recording. Simpler but produces a different viewing experience (one continuous clip with camera cuts, not separate angles sequentially).
- **Ship without stitching first** is a viable MVP for Part 2: deliver individual clips, let the user combine manually or upload separately. Add stitching as a fast-follow.
- Consider bundling a lightweight FFmpeg binary with the plugin, or detecting if FFmpeg is on the user's PATH.
