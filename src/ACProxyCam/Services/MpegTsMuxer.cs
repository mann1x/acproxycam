// MpegTsMuxer.cs - Memory-efficient MPEG-TS muxer for HLS streaming
// Creates MPEG-TS segments from H.264 NAL units with minimal allocations

using System.Buffers;

namespace ACProxyCam.Services;

/// <summary>
/// Memory-efficient MPEG-TS muxer for creating HLS segments from H.264 NAL units.
/// Uses ArrayPool to minimize allocations and GC pressure.
/// </summary>
public class MpegTsMuxer
{
    // MPEG-TS constants
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;

    // PID assignments
    private const ushort PatPid = 0x0000;
    private const ushort PmtPid = 0x1000;
    private const ushort VideoPid = 0x0100;

    // Stream type
    private const byte H264StreamType = 0x1B;

    // Pre-allocated buffers
    private readonly byte[] _tsPacket = new byte[TsPacketSize];
    private readonly byte[] _pesHeader = new byte[14];
    private readonly byte[] _startCode = { 0x00, 0x00, 0x00, 0x01 };

    // Continuity counters (0-15, wrap around)
    private byte _patCc;
    private byte _pmtCc;
    private byte _videoCc;

    // Current PTS - start at 90000 (1 second) to avoid timestamp 0 issues
    // Uses synthetic PTS based on wall-clock frame rate for accurate HLS timing
    private long _currentPts = 0;
    private long _ptsIncrement = 3600; // Default ~25fps (90000/25)

    // Track segment start PTS for duration calculation
    private long _segmentStartPts = -1;
    private long _lastPts = 0;

    // H.264 parameters
    private byte[]? _spsNal;
    private byte[]? _ppsNal;

    // CRC32 lookup table for MPEG-TS
    private static readonly uint[] Crc32Table = GenerateCrc32Table();

    /// <summary>
    /// Set H.264 SPS/PPS NAL units.
    /// </summary>
    public void SetH264Parameters(byte[]? sps, byte[]? pps)
    {
        _spsNal = sps;
        _ppsNal = pps;
    }

    /// <summary>
    /// Set the PTS increment per frame (based on frame rate).
    /// </summary>
    public void SetFrameRate(double fps)
    {
        if (fps <= 0) fps = 25.0;
        _ptsIncrement = (long)(90000.0 / fps);
    }

    /// <summary>
    /// Called at HLS segment boundaries.
    /// PTS continues to grow continuously - no reset needed.
    /// CCs continue across segments per MPEG-TS spec (not reset).
    /// </summary>
    public void ResetPts()
    {
        // Only reset segment duration tracker
        // PTS continues growing for continuous playback
        _segmentStartPts = -1;
    }

    /// <summary>
    /// Get the duration of the current segment in seconds based on actual PTS values.
    /// Returns 0 if no frames have been written.
    /// </summary>
    public double GetSegmentDuration()
    {
        if (_segmentStartPts < 0 || _lastPts <= _segmentStartPts)
            return 0;
        // Convert from 90kHz to seconds
        return (_lastPts - _segmentStartPts) / 90000.0;
    }

    /// <summary>
    /// Get the PTS value at the start of the current segment (in 90kHz ticks).
    /// Returns -1 if no frames have been written yet.
    /// </summary>
    public long GetSegmentStartPts() => _segmentStartPts;

    /// <summary>
    /// Full reset for stream restart (not segment boundary).
    /// </summary>
    public void Reset()
    {
        _patCc = 0;
        _pmtCc = 0;
        _videoCc = 0;
        _currentPts = 0;
        _segmentStartPts = -1;
        _lastPts = 0;
    }

    /// <summary>
    /// Write PAT and PMT packets to output stream.
    /// </summary>
    public void WritePatPmt(Stream output)
    {
        WritePatPacket(output);
        WritePmtPacket(output);
    }

    /// <summary>
    /// Write a video frame to the output stream as MPEG-TS packets.
    /// NAL units should be raw H.264 NAL data (without start codes or length prefixes).
    /// </summary>
    /// <param name="output">Output stream</param>
    /// <param name="nalData">Raw NAL unit data</param>
    /// <param name="isKeyframe">Whether this is a keyframe</param>
    /// <param name="writePatPmt">Whether to write PAT/PMT tables</param>
    /// <param name="sourcePts">Source PTS from decoder (ignored - we use synthetic PTS)</param>
    /// <param name="discontinuity">Set discontinuity indicator (segment boundary)</param>
    /// <param name="isFirstNalOfFrame">True if this is the first NAL of a frame (for PTS increment)</param>
    /// <param name="forceSpsPps">Force SPS/PPS even for non-keyframes (segment start)</param>
    public void WriteFrame(Stream output, ReadOnlySpan<byte> nalData, bool isKeyframe, bool writePatPmt = false, long sourcePts = 0, bool discontinuity = false, bool isFirstNalOfFrame = true, bool forceSpsPps = false)
    {
        if (writePatPmt || isKeyframe)
        {
            WritePatPacket(output);
            WritePmtPacket(output);
        }

        // ALWAYS use synthetic PTS based on wall-clock estimated frame rate
        // Source PTS from cameras is often unreliable and doesn't match real-time
        // Using synthetic PTS ensures MPEG-TS timing matches HLS playlist durations
        // (which are also based on wall-clock frame count / fps)
        long pts = _currentPts;

        // Only increment PTS on first NAL of each frame
        // Multiple NALs in same frame (SPS, PPS, IDR) share the same PTS
        if (isFirstNalOfFrame)
        {
            _currentPts += _ptsIncrement;
        }

        // Track segment timing
        if (_segmentStartPts < 0)
        {
            _segmentStartPts = pts;
        }
        _lastPts = pts;

        // Include SPS/PPS for keyframes or when forced (segment start with non-keyframe)
        bool includeSpsPps = (isKeyframe || forceSpsPps) && _spsNal != null && _ppsNal != null;

        // Calculate total PES payload size
        int payloadSize = nalData.Length + 4; // NAL + start code
        if (includeSpsPps)
        {
            payloadSize += _spsNal!.Length + 4 + _ppsNal!.Length + 4; // SPS + PPS with start codes
        }

        // Build PES header
        BuildPesHeader(pts, isKeyframe);

        // Write TS packets - pass the frame's PTS for PCR
        WriteVideoTsPackets(output, nalData, isKeyframe || forceSpsPps, payloadSize, discontinuity, pts);
    }

    /// <summary>
    /// Write PAT (Program Association Table) packet.
    /// </summary>
    private void WritePatPacket(Stream output)
    {
        Array.Clear(_tsPacket);
        int offset = 0;

        // TS header
        _tsPacket[offset++] = SyncByte;
        _tsPacket[offset++] = 0x40; // Payload unit start + PID high (0)
        _tsPacket[offset++] = 0x00; // PID low (0 = PAT)
        _tsPacket[offset++] = (byte)(0x10 | (_patCc & 0x0F));
        _patCc = (byte)((_patCc + 1) & 0x0F);

        // Pointer field
        _tsPacket[offset++] = 0x00;

        // PAT section
        int sectionStart = offset;
        _tsPacket[offset++] = 0x00; // Table ID (PAT)
        _tsPacket[offset++] = 0xB0; // Section syntax + section length high
        _tsPacket[offset++] = 13;   // Section length
        _tsPacket[offset++] = 0x00; // Transport stream ID high
        _tsPacket[offset++] = 0x01; // Transport stream ID low
        _tsPacket[offset++] = 0xC1; // Reserved + version + current/next
        _tsPacket[offset++] = 0x00; // Section number
        _tsPacket[offset++] = 0x00; // Last section number

        // Program (program_number=1 -> PMT PID)
        _tsPacket[offset++] = 0x00;
        _tsPacket[offset++] = 0x01;
        _tsPacket[offset++] = (byte)(0xE0 | ((PmtPid >> 8) & 0x1F));
        _tsPacket[offset++] = (byte)(PmtPid & 0xFF);

        // CRC32
        var crc = CalculateCrc32(_tsPacket, sectionStart, offset - sectionStart);
        _tsPacket[offset++] = (byte)((crc >> 24) & 0xFF);
        _tsPacket[offset++] = (byte)((crc >> 16) & 0xFF);
        _tsPacket[offset++] = (byte)((crc >> 8) & 0xFF);
        _tsPacket[offset++] = (byte)(crc & 0xFF);

        // Fill rest with 0xFF
        for (int i = offset; i < TsPacketSize; i++)
            _tsPacket[i] = 0xFF;

        output.Write(_tsPacket, 0, TsPacketSize);
    }

    /// <summary>
    /// Write PMT (Program Map Table) packet.
    /// </summary>
    private void WritePmtPacket(Stream output)
    {
        Array.Clear(_tsPacket);
        int offset = 0;

        // TS header
        _tsPacket[offset++] = SyncByte;
        _tsPacket[offset++] = (byte)(0x40 | ((PmtPid >> 8) & 0x1F));
        _tsPacket[offset++] = (byte)(PmtPid & 0xFF);
        _tsPacket[offset++] = (byte)(0x10 | (_pmtCc & 0x0F));
        _pmtCc = (byte)((_pmtCc + 1) & 0x0F);

        // Pointer field
        _tsPacket[offset++] = 0x00;

        // PMT section
        int sectionStart = offset;
        _tsPacket[offset++] = 0x02; // Table ID (PMT)
        _tsPacket[offset++] = 0xB0; // Section syntax + section length high
        _tsPacket[offset++] = 18;   // Section length
        _tsPacket[offset++] = 0x00; // Program number high
        _tsPacket[offset++] = 0x01; // Program number low
        _tsPacket[offset++] = 0xC1; // Reserved + version + current/next
        _tsPacket[offset++] = 0x00; // Section number
        _tsPacket[offset++] = 0x00; // Last section number
        _tsPacket[offset++] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)); // PCR PID high
        _tsPacket[offset++] = (byte)(VideoPid & 0xFF);
        _tsPacket[offset++] = 0xF0; // Program info length high
        _tsPacket[offset++] = 0x00; // Program info length low

        // H.264 video stream
        _tsPacket[offset++] = H264StreamType;
        _tsPacket[offset++] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F));
        _tsPacket[offset++] = (byte)(VideoPid & 0xFF);
        _tsPacket[offset++] = 0xF0;
        _tsPacket[offset++] = 0x00;

        // CRC32
        var crc = CalculateCrc32(_tsPacket, sectionStart, offset - sectionStart);
        _tsPacket[offset++] = (byte)((crc >> 24) & 0xFF);
        _tsPacket[offset++] = (byte)((crc >> 16) & 0xFF);
        _tsPacket[offset++] = (byte)((crc >> 8) & 0xFF);
        _tsPacket[offset++] = (byte)(crc & 0xFF);

        // Fill rest with 0xFF
        for (int i = offset; i < TsPacketSize; i++)
            _tsPacket[i] = 0xFF;

        output.Write(_tsPacket, 0, TsPacketSize);
    }

    /// <summary>
    /// Build PES header into pre-allocated buffer.
    /// </summary>
    private void BuildPesHeader(long pts, bool isKeyframe)
    {
        int offset = 0;

        // Packet start code prefix
        _pesHeader[offset++] = 0x00;
        _pesHeader[offset++] = 0x00;
        _pesHeader[offset++] = 0x01;

        // Stream ID (0xE0 = video)
        _pesHeader[offset++] = 0xE0;

        // PES packet length (0 = unbounded for video)
        _pesHeader[offset++] = 0x00;
        _pesHeader[offset++] = 0x00;

        // Flags
        _pesHeader[offset++] = (byte)(0x80 | (isKeyframe ? 0x04 : 0x00));
        _pesHeader[offset++] = 0x80; // PTS only

        // PES header data length (5 bytes for PTS)
        _pesHeader[offset++] = 0x05;

        // PTS (5 bytes)
        _pesHeader[offset++] = (byte)(0x21 | ((int)((pts >> 30) & 0x07) << 1));
        _pesHeader[offset++] = (byte)((pts >> 22) & 0xFF);
        _pesHeader[offset++] = (byte)(0x01 | ((int)((pts >> 15) & 0x7F) << 1));
        _pesHeader[offset++] = (byte)((pts >> 7) & 0xFF);
        _pesHeader[offset++] = (byte)(0x01 | ((int)((pts >> 0) & 0x7F) << 1));
    }

    /// <summary>
    /// Write video data as TS packets with adaptation fields.
    /// </summary>
    /// <param name="framePts">The frame's PTS value to use for PCR</param>
    private void WriteVideoTsPackets(Stream output, ReadOnlySpan<byte> nalData, bool isKeyframe, int totalPayloadSize, bool discontinuity, long framePts)
    {
        // Calculate total data: PES header + payload
        int totalSize = _pesHeader.Length + totalPayloadSize;

        // Safety limit: max 1000 TS packets per frame (188KB)
        const int maxPackets = 1000;
        int packetCount = 0;

        // We need to write this data across multiple TS packets
        int dataWritten = 0;
        bool isFirst = true;

        // Data sources in order: PES header, SPS (if keyframe), PPS (if keyframe), start code, NAL data
        int pesHeaderOffset = 0;
        int spsOffset = 0;
        int ppsOffset = 0;
        int nalOffset = 0;
        int phase = 0; // 0=PES header, 1=SPS start code, 2=SPS, 3=PPS start code, 4=PPS, 5=NAL start code, 6=NAL

        bool hasSps = isKeyframe && _spsNal != null && _ppsNal != null;

        while (dataWritten < totalSize && packetCount < maxPackets)
        {
            Array.Clear(_tsPacket);
            int packetOffset = 0;

            // Calculate remaining data
            int remaining = totalSize - dataWritten;

            // Determine adaptation field needs
            // Write PCR on EVERY frame's first packet for proper clock sync
            bool needsPcr = isFirst;
            int adaptationLength = 0;

            if (needsPcr)
            {
                adaptationLength = 8; // Flags + PCR (7 bytes) + 1 for length byte itself handled below
            }

            // Calculate available payload space
            // TS packet: 4 byte header + optional adaptation field + payload = 188 bytes
            int headerSize = 4 + (needsPcr ? 1 + 7 : 0); // TS header + adaptation (if PCR)
            int maxPayload = TsPacketSize - headerSize;

            // Check if we need stuffing (only for LAST packet when remaining < maxPayload)
            if (remaining < maxPayload)
            {
                // Need stuffing via adaptation field to fill the 188-byte packet
                // Adaptation field: 1 byte length + 1 byte flags + N stuffing bytes
                // We need: 4 (header) + 1 (adapt len) + adapt_content + remaining = 188
                // So adapt_content = 188 - 4 - 1 - remaining = 183 - remaining
                // adapt_content includes flags (1 byte) + PCR (6 bytes if needed) + stuffing

                if (needsPcr)
                {
                    // PCR takes 7 bytes (1 flag + 6 PCR), so stuffing = 183 - remaining - 7
                    int stuffingNeeded = 183 - remaining - 7;
                    if (stuffingNeeded < 0) stuffingNeeded = 0;
                    adaptationLength = 7 + stuffingNeeded; // flags + PCR + stuffing
                }
                else
                {
                    // Just flags + stuffing: 183 - remaining bytes total
                    int stuffingNeeded = 183 - remaining - 1; // -1 for flags byte
                    if (stuffingNeeded < 0) stuffingNeeded = 0;
                    adaptationLength = 1 + stuffingNeeded; // flags + stuffing
                }
                headerSize = 4 + 1 + adaptationLength;
                maxPayload = remaining; // Exactly fill with remaining data
            }

            // TS header
            _tsPacket[packetOffset++] = SyncByte;
            _tsPacket[packetOffset++] = (byte)((isFirst ? 0x40 : 0x00) | ((VideoPid >> 8) & 0x1F));
            _tsPacket[packetOffset++] = (byte)(VideoPid & 0xFF);

            byte flags = (byte)(_videoCc & 0x0F);
            if (adaptationLength > 0)
                flags |= 0x30; // Adaptation + payload
            else
                flags |= 0x10; // Payload only
            _tsPacket[packetOffset++] = flags;
            _videoCc = (byte)((_videoCc + 1) & 0x0F);

            // Adaptation field
            if (adaptationLength > 0)
            {
                _tsPacket[packetOffset++] = (byte)adaptationLength;

                // Adaptation flags
                byte adaptFlags = 0;
                if (discontinuity && isFirst)
                {
                    adaptFlags |= 0x80; // Discontinuity indicator
                }
                if (needsPcr)
                {
                    adaptFlags |= 0x10; // PCR present
                    // Random access indicator ONLY for keyframes (allows seeking to this point)
                    if (isKeyframe)
                    {
                        adaptFlags |= 0x40; // Random access indicator
                    }
                }
                _tsPacket[packetOffset++] = adaptFlags;

                // PCR (if present) - use the frame's PTS for accurate clock reference
                if (needsPcr)
                {
                    // PCR must equal PTS for proper sync - use passed framePts
                    long pcr = framePts;
                    _tsPacket[packetOffset++] = (byte)((pcr >> 25) & 0xFF);
                    _tsPacket[packetOffset++] = (byte)((pcr >> 17) & 0xFF);
                    _tsPacket[packetOffset++] = (byte)((pcr >> 9) & 0xFF);
                    _tsPacket[packetOffset++] = (byte)((pcr >> 1) & 0xFF);
                    _tsPacket[packetOffset++] = (byte)((((int)(pcr & 0x01)) << 7) | 0x7E);
                    _tsPacket[packetOffset++] = 0x00;
                }

                // Stuffing bytes
                while (packetOffset < 4 + 1 + adaptationLength)
                    _tsPacket[packetOffset++] = 0xFF;
            }

            // Write payload data
            int payloadWritten = 0;
            int payloadSpace = TsPacketSize - packetOffset;

            while (payloadWritten < payloadSpace && dataWritten < totalSize)
            {
                int bytesToCopy = 0;
                ReadOnlySpan<byte> source = default;

                switch (phase)
                {
                    case 0: // PES header
                        source = _pesHeader.AsSpan(pesHeaderOffset);
                        bytesToCopy = Math.Min(source.Length, payloadSpace - payloadWritten);
                        pesHeaderOffset += bytesToCopy;
                        if (pesHeaderOffset >= _pesHeader.Length) phase = hasSps ? 1 : 5;
                        break;

                    case 1: // SPS start code
                        source = _startCode;
                        bytesToCopy = Math.Min(source.Length - spsOffset, payloadSpace - payloadWritten);
                        if (spsOffset + bytesToCopy >= _startCode.Length) { phase = 2; spsOffset = 0; }
                        else spsOffset += bytesToCopy;
                        break;

                    case 2: // SPS data
                        source = _spsNal.AsSpan(spsOffset);
                        bytesToCopy = Math.Min(source.Length, payloadSpace - payloadWritten);
                        spsOffset += bytesToCopy;
                        if (spsOffset >= _spsNal!.Length) { phase = 3; spsOffset = 0; }
                        break;

                    case 3: // PPS start code
                        source = _startCode;
                        bytesToCopy = Math.Min(source.Length - ppsOffset, payloadSpace - payloadWritten);
                        if (ppsOffset + bytesToCopy >= _startCode.Length) { phase = 4; ppsOffset = 0; }
                        else ppsOffset += bytesToCopy;
                        break;

                    case 4: // PPS data
                        source = _ppsNal.AsSpan(ppsOffset);
                        bytesToCopy = Math.Min(source.Length, payloadSpace - payloadWritten);
                        ppsOffset += bytesToCopy;
                        if (ppsOffset >= _ppsNal!.Length) { phase = 5; ppsOffset = 0; }
                        break;

                    case 5: // NAL start code
                        source = _startCode;
                        bytesToCopy = Math.Min(source.Length - nalOffset, payloadSpace - payloadWritten);
                        if (nalOffset + bytesToCopy >= _startCode.Length) { phase = 6; nalOffset = 0; }
                        else nalOffset += bytesToCopy;
                        break;

                    case 6: // NAL data
                        source = nalData.Slice(nalOffset);
                        bytesToCopy = Math.Min(source.Length, payloadSpace - payloadWritten);
                        nalOffset += bytesToCopy;
                        break;
                }

                if (bytesToCopy > 0)
                {
                    source.Slice(0, bytesToCopy).CopyTo(_tsPacket.AsSpan(packetOffset + payloadWritten));
                    payloadWritten += bytesToCopy;
                    dataWritten += bytesToCopy;
                }
                else
                {
                    break;
                }
            }

            output.Write(_tsPacket, 0, TsPacketSize);
            isFirst = false;
            packetCount++;
        }
    }

    /// <summary>
    /// Calculate CRC32 for MPEG-TS tables.
    /// </summary>
    private static uint CalculateCrc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < length; i++)
        {
            crc = (crc << 8) ^ Crc32Table[((crc >> 24) ^ data[offset + i]) & 0xFF];
        }
        return crc;
    }

    /// <summary>
    /// Generate CRC32 lookup table.
    /// </summary>
    private static uint[] GenerateCrc32Table()
    {
        var table = new uint[256];
        const uint polynomial = 0x04C11DB7;

        for (uint i = 0; i < 256; i++)
        {
            uint crc = i << 24;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x80000000) != 0)
                    crc = (crc << 1) ^ polynomial;
                else
                    crc <<= 1;
            }
            table[i] = crc;
        }

        return table;
    }
}
