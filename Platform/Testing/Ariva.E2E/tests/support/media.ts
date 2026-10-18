import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// ARV-142b: the advertisement video fixtures (Platform/Testing/Media, two-second test patterns made with ffmpeg; its
// README says how) and the byte patches the specs make of them.

const media = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'Media');

/** A fixture video, read afresh (a patch never reaches another test). */
export function videoFixture(name: 'h264-aac.mp4' | 'h264-moov-last.mp4' | 'vp9-opus.webm' | 'vp8-vorbis.webm' | 'av1.webm'): Buffer {
	return fs.readFileSync(path.join(media, name));
}

/** An MP4 box of this type around the payload (a 32-bit size). */
export function box(type: string, payload: Buffer | string): Buffer {
	const body = Buffer.isBuffer(payload) ? payload : Buffer.from(payload);
	const header = Buffer.alloc(8);
	header.writeUInt32BE(8 + body.length, 0);
	header.write(type, 4, 'latin1');
	return Buffer.concat([header, body]);
}

/** The MP4 with its sample entry's codec renamed (the file type box names avc1 among its brands too, so after stsd). */
export function withCodec(mp4: Buffer, codec: string): Buffer {
	const copy = Buffer.from(mp4);
	const description = copy.indexOf('stsd', 0, 'latin1');
	copy.write(codec, copy.indexOf('avc1', description, 'latin1'), 'latin1');
	return copy;
}

/** The MP4 with its movie's length set in milliseconds (ffmpeg's movie timescale is 1,000 a second). */
export function lasting(mp4: Buffer, milliseconds: number): Buffer {
	const copy = Buffer.from(mp4);
	const header = copy.indexOf('mvhd', 0, 'latin1') + 4;
	if (copy.readUInt32BE(header + 12) !== 1000) throw new Error('not the fixture this patch expects');
	copy.writeUInt32BE(milliseconds, header + 16);
	return copy;
}

/** The MP4 with a top-level box renamed (the first one of that type). */
export function renamed(mp4: Buffer, from: string, to: string): Buffer {
	const copy = Buffer.from(mp4);
	copy.write(to, copy.indexOf(from, 0, 'latin1'), 'latin1');
	return copy;
}

/** The WebM with its segment's size made unknown, as a live recording writes it. */
export function live(webm: Buffer): Buffer {
	const copy = Buffer.from(webm);
	const segment = copy.indexOf(Buffer.from([0x18, 0x53, 0x80, 0x67]));
	copy[segment + 4] = 0x01;
	copy.fill(0xff, segment + 5, segment + 12);
	return copy;
}
