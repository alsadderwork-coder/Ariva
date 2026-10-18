# Advertisement video fixtures (ARV-142b)

Two-second test patterns made with ffmpeg 6.1 from its built-in sources (`testsrc` and `sine`): no camera footage, no
people, no client material. The unit tests (`Ariva.UnitTests/Storage/AdvertisingVideosTests.cs`, which copy them to their
output as `Media/`) and the E2E specs (`tests/api/advertising-media.spec.ts`, `tests/functional/advertising-media.spec.ts`)
read them; CI needs no ffmpeg. Each is a few kilobytes, 160 by 90 pixels.

| File | Picture | Sound | Notes |
|---|---|---|---|
| `h264-aac.mp4` | H.264 baseline | AAC | `moov` first (`+faststart`), a title and a comment in its user data |
| `h264-moov-last.mp4` | H.264 | none | `moov` after the media data |
| `vp9-opus.webm` | VP9 | Opus | a title in the segment information, the encoder's tags |
| `vp8-vorbis.webm` | VP8 | Vorbis | |
| `av1.webm` | AV1 | none | |

```
ffmpeg -f lavfi -i testsrc=size=160x90:rate=15 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 2 -c:v libx264 -pix_fmt yuv420p -profile:v baseline -preset veryfast -b:v 60k -c:a aac -b:a 24k -movflags +faststart -metadata title="Fixture title <b>title</b>" -metadata comment="fixture comment" h264-aac.mp4
ffmpeg -f lavfi -i testsrc=size=160x90:rate=15 -t 2 -c:v libx264 -pix_fmt yuv420p -preset veryfast -b:v 60k h264-moov-last.mp4
ffmpeg -f lavfi -i testsrc=size=160x90:rate=15 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 2 -c:v libvpx-vp9 -b:v 60k -deadline realtime -cpu-used 8 -c:a libopus -b:a 24k -metadata title="Fixture title <b>title</b>" vp9-opus.webm
ffmpeg -f lavfi -i testsrc=size=160x90:rate=15 -f lavfi -i sine=frequency=440:sample_rate=44100 -t 2 -c:v libvpx -b:v 60k -deadline realtime -cpu-used 8 -c:a libvorbis -q:a 0 vp8-vorbis.webm
ffmpeg -f lavfi -i testsrc=size=160x90:rate=15 -t 2 -c:v libsvtav1 -preset 12 -b:v 60k av1.webm
```
