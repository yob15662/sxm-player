# AAC Frame and ICY Metadata Logic

## Overview
The stream pipeline serves HLS AAC (`.aac`) segment data over an Icecast-compatible HTTP response.

The important rule is:
- When ICY metadata is enabled, metadata **must** be injected at the exact configured `icy-metaint` byte interval.

If metadata is not injected at the exact interval, ICY clients read the wrong byte as metadata length, which shifts parsing and causes AAC decode failures (for example: `PCE shall be the first element in a frame`).

A newly observed decoder failure in MPD/FAAD is:
- `faad_decoder: error decoding AAC stream: Bitstream value not allowed by specification`

This is typically the same class of issue: the decoder is reading invalid AAC syntax because frame boundaries were shifted or ADTS bytes were corrupted in transit.

## Components

- `AacFrameAnalyzer`
  - Detects ADTS sync words (`0xFFF`)
  - Parses ADTS frame length
  - Finds the next valid frame boundary for recovery when data is misaligned

- `IcyStreamWriter`
  - Writes audio bytes to the HTTP response
  - Injects ICY metadata blocks at exact `metadataInterval` boundaries
  - Can skip leading non-ADTS bytes and resume at the first detected frame boundary

- `IcyMetadataBuilder`
  - Builds ICY metadata blocks in standard format:
    - 1 length byte (count of 16-byte blocks)
    - metadata payload padded to 16-byte boundaries

## Write Flow (`IcyStreamWriter`)

1. If metadata is disabled:
   - Write audio in chunks (`OutputChunkSize`)
   - Flush and return

2. If metadata is enabled:
   - Validate `metadataInterval > 0`
   - Normalize `bytesUntilNextMetadata` into `[1..metadataInterval]`
   - Attempt initial ADTS resync with `AacFrameAnalyzer.FindNextFrameBoundary`
   - Loop until all audio is written:
     - If `bytesUntilNextMetadata == 0`:
       - Write an ICY metadata block
       - Reset budget to `metadataInterval`
     - Write up to `min(audioRemaining, bytesUntilNextMetadata, OutputChunkSize)` audio bytes
     - Decrement `bytesUntilNextMetadata`
   - Flush and return remaining byte budget

## Why this avoids corruption

By ensuring metadata is inserted exactly at the configured interval, ICY-aware clients can always:
1. read exactly `icy-metaint` audio bytes,
2. read one metadata-length byte,
3. skip metadata payload,
4. continue reading aligned AAC bytes.

This preserves byte alignment in the client decoder and prevents AAC parser desynchronization.

## MPD/FAAD Error Mapping

When MPD logs `Bitstream value not allowed by specification`, treat it as a strong signal that one of these happened:

1. Metadata alignment drifted
   - Metadata byte inserted earlier/later than `icy-metaint`.
   - Client interpreted AAC bytes as metadata length (or vice versa).

2. Incomplete/truncated ADTS frame delivery
   - A frame boundary was cut or resumed incorrectly across writes.

3. Non-ADTS bytes leaked into audio payload
   - Leading bytes or transport artifacts were not resynced before decode.

## Validation Checklist

- Confirm `icy-metaint` sent in headers matches writer interval exactly.
- Confirm metadata block format is valid (`length byte + padded payload`).
- Confirm audio-byte counter resets only after metadata write.
- Confirm ADTS resync occurs before first audio bytes when stream start is not aligned.
- Confirm no extra bytes are inserted between ADTS frames besides ICY metadata blocks at interval boundaries.

If all checks pass, MPD/FAAD should stop hitting this bitstream-spec parsing error for alignment-related cases.
