# Encoder Error Catalog

Source of truth for every `EncoderRuleId` and `EncoderRuntimeErrorId` constant the encoder pipeline can emit.
Every ID listed here corresponds to a `public const string` declared in `src/NoMercy.Encoder/Errors/EncoderRuleId.cs`.
The runtime subset is re-exported from `src/NoMercy.Encoder/Errors/EncoderRuntimeErrorId.cs` for controllers and middleware.

The reflection-driven test in `tests/NoMercy.Tests.Encoder/Errors/RuntimeErrorCatalogTests.cs` asserts every constant
shows up below wrapped in backticks. Add a new ID → add a new bullet here in the matching section.

## Audio
- `audio.ac3_off_ladder_bitrate` — AC-3 bitrate is not on the standard ladder.
- `audio.bitrate_missing` — lossy audio encoder configured with bitrate_kbps = 0; needs a positive target.
- `audio.codec_container_mismatch` — audio codec is not allowed by the chosen container.
- `audio.eac3_off_ladder_bitrate` — E-AC-3 bitrate is not on the standard ladder.

## Bit depth
- `bit_depth.auto_downgrade` — 10-bit requested with no hardware support; auto-downgraded to 8-bit.
- `bit_depth.no_hardware_support` — selected encoder cannot encode the requested bit depth.
- `bit_depth.strict_violation` — strict policy refuses to silently downgrade.
- `bit_depth.vp9_profile_mismatch` — VP9 profile and bit depth do not agree (profile 0/1 = 8-bit, 2/3 = 10/12-bit).
- `bit_depth.h26x_profile_promoted` — an 8-bit-only H.264/H.265 profile was requested with 10-bit depth; the profile is promoted to its 10-bit sibling (High to High10, Main to Main10) so the encoder accepts the 10-bit pixel format.

## Bitrate
- `bitrate.too_low_for_resolution` — codec-aware floor not met for the chosen resolution.

## Capabilities
- `capability.fpcalc_missing` — chromaprint binary not on PATH; intro detection disabled.
- `capability.stemsplit_missing` — stemsplit GGUF model file missing or not configured; stem separation disabled.
- `capability.tesseract_model_missing` — required Tesseract `*.traineddata` not in the configured directory.
- `capability.whisper_missing` — whisper-cli binary not on PATH; speech transcription disabled.

## Codec / container
- `codec.container_mismatch` — video codec is not allowed by the chosen container.

## CRF / rate control
- `crf.out_of_typical_range` — CRF lies outside the typical 18–28 band for the codec.

## Custom arguments
- `custom_args.reserved_flag` — extra ffmpeg flag collides with an encoder-managed argument.

## Disc ripping
- `disc.aacs_cert_missing` — KEYDB lacks a matching AACS certificate for this volume.
- `disc.bdplus_converter_missing` — BD+ converter not present for this volume.
- `disc.drive_busy` — another rip is already running on this physical drive.
- `disc.read_error` — generic read failure surfaced from libbluray / libdvdread.

## Distribution
- `distribution.hmac_invalid` — HMAC signature failed verification on the inbound request.
- `distribution.timestamp_replay` — request timestamp is outside the allowed replay window.
- `distribution.worker_not_registered` — coordinator received a heartbeat / progress push from an unknown worker id.

## DRM
- `drm.http_not_https` — DRM key URI must be HTTPS.
- `drm.key_missing` — encryption requested but no key configured.

## Encoder runtime
- `encoder.init_failed` — encoder handle could not be initialised (driver fault, missing kernel module, etc.).
- `gpu_capacity_exhausted` — concurrent encode-session cap reached for the selected GPU.
- `hardware.forced_but_unavailable` — `force_hardware` requested but no hardware encoder is reachable.
- `hardware.gpu_telemetry_unsupported` — vendor SDK not present; per-GPU utilization unavailable.

## HDR
- `hdr.inverse_tonemap_unsupported` — SDR-to-HDR conversion is not supported.

## HLS
- `hls.fmp4_codec_mismatch` — fMP4 segments require h264/hevc/av1; the chosen codec is not compatible.
- `hls.keyframe_segment_misalignment` — segment duration is not an integer multiple of the keyframe interval.

## Imports / signatures
- `import.fetch_failed` — profile URL was reachable but the download failed (non-2xx status, timeout, or transport error).
- `import.http_not_https` — profile import URL must be HTTPS.
- `import.json_malformed` — fetched or inline profile body is not valid JSON, or deserialises to null.
- `import.publisher_untrusted` — profile signed by a key that is not in the trusted publishers table.
- `import.signature_invalid` — profile signature did not verify against the declared publisher key.
- `import.source_missing` — neither an inline profile body nor a URL was supplied to import from.
- `import.unsigned_requires_flag` — unsigned profile import requires the explicit `?trust_unsigned=true` query flag.

## Jobs
- `job.interrupted_no_checkpoint` — an orphaned job was found at startup with no checkpoint to resume from.

## Ladder
- `ladder.duplicate_variant` — two variants resolve to identical codec/resolution/bitrate/CRF/bit-depth.
- `ladder.inverted` — manual ladder is not monotonically ordered by bitrate or resolution.
- `ladder.manual_empty` — Manual ladder mode declared with an empty rungs[] array.
- `ladder.manual_unsorted` — manual ladder rungs are not sorted ascending by bitrate.

## Levels
- `level.frame_rate_cap_exceeded` — the source fps × resolution exceeds the declared codec level's luma sample rate cap; the encoder will reject it at runtime.
- `level.invalid` — the declared level is not a level the codec defines; ffmpeg would reject it.
- `level.resolution_mismatch` — codec level cannot carry the requested resolution.

## Licensing
- `license.revoked` — cluster token request returned 403.
- `license.unreachable` — cluster token endpoint failed to respond.

## Output
- `output.path_not_allowed` — write target falls outside the allowlisted root.
- `output.write_error` — generic write failure (full disk, permission denied, EIO).

## Profile
- `parent_id.cycle` — profile inheritance forms a cycle.
- `profile.builtin_readonly` — built-in presets cannot be edited; clone first.
- `profile.name_missing` — profile must declare a name.
- `profile.no_outputs` — profile must declare at least one video or audio output.

## Source analysis
- `source.dolby_vision_will_be_stripped` — DV cannot be preserved through the requested target.
- `source.not_accessible` — source file path is not reachable from this worker.
- `source.read_error` — generic read failure on the source.
- `source.spherical_metadata_will_be_stripped` — VR projection metadata (sv3d/Projection) will be lost if the video stream is re-encoded; switch to stream-copy to retain it.
- `source.stereoscopic_unsupported` — 3D stereo source detected but re-encoding 3D is not supported; use a stream-copy profile to preserve stereo_mode.
- `source.upscaling_detected` — target resolution exceeds source resolution.
- `source.variable_frame_rate` — VFR detected; emitting a warning, not gating the encode.

## Subtitles
- `subtitles.ass_needs_capable_client` — ASS chosen; informational note.
- `subtitles.burn_in_permanent` — burn-in cannot be undone post-encode.
- `subtitles.container_incompatible` — bitmap subtitles cannot ride the chosen container without OCR.

## Trusted publishers
- `trusted_publisher.already_trusted` — fingerprint is already registered.
- `trusted_publisher.public_key_invalid` — supplied public key is not a parseable PEM/DER blob.

## Video
- `video.height_invalid` — height must be a positive even integer.
- `video.rate_control_conflict` — both CRF and bitrate set.
- `video.rate_control_missing` — neither CRF nor bitrate set.
- `video.width_invalid` — width must be a positive even integer.
