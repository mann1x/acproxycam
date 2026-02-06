// FlvMuxer.cs - FLV muxer for H.264 streaming
// Converts AVCC-format H.264 packets into FLV container format.
// Compatible with Anycubic slicer's /flv endpoint (gkcam format).

using System.Buffers.Binary;

namespace ACProxyCam.Services;

/// <summary>
/// Muxes H.264 AVCC packets into FLV container format.
/// Thread-safe for concurrent client writes.
/// </summary>
public class FlvMuxer
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _frameDurationMs;
    private int _timestamp;
    private bool _hasSentDecoderConfig;

    // Cached decoder config (AVC sequence header tag)
    private byte[]? _decoderConfigTag;

    public FlvMuxer(int width, int height, int fps)
    {
        _width = width > 0 ? width : 1920;
        _height = height > 0 ? height : 1080;
        _fps = fps > 0 ? fps : 15;
        _frameDurationMs = 1000 / _fps;
    }

    /// <summary>
    /// Create FLV file header (13 bytes: 9-byte header + 4-byte PreviousTagSize0).
    /// </summary>
    public static byte[] CreateHeader()
    {
        var header = new byte[13];
        header[0] = (byte)'F';
        header[1] = (byte)'L';
        header[2] = (byte)'V';
        header[3] = 1;    // Version
        header[4] = 0x01; // Flags: has video
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), 9); // Header length
        // PreviousTagSize0 = 0 (bytes 9-12, already zeroed)
        return header;
    }

    /// <summary>
    /// Create FLV onMetaData script tag with video metadata.
    /// </summary>
    public byte[] CreateMetadataTag()
    {
        using var ms = new MemoryStream(256);

        // AMF0 String: "onMetaData"
        ms.WriteByte(0x02); // String type
        WriteAmfString(ms, "onMetaData");

        // AMF0 ECMA Array
        ms.WriteByte(0x08); // ECMA array type
        WriteUInt32BE(ms, 5); // approximate element count

        // width
        WriteAmfString(ms, "width");
        ms.WriteByte(0x00); // Number type
        WriteDoubleBE(ms, _width);

        // height
        WriteAmfString(ms, "height");
        ms.WriteByte(0x00);
        WriteDoubleBE(ms, _height);

        // framerate
        WriteAmfString(ms, "framerate");
        ms.WriteByte(0x00);
        WriteDoubleBE(ms, _fps);

        // videocodecid = 7 (AVC/H.264)
        WriteAmfString(ms, "videocodecid");
        ms.WriteByte(0x00);
        WriteDoubleBE(ms, 7.0);

        // duration = 0 (live stream)
        WriteAmfString(ms, "duration");
        ms.WriteByte(0x00);
        WriteDoubleBE(ms, 0.0);

        // End of object marker
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte(0x09);

        return CreateTag(18, ms.ToArray(), 0); // Tag type 18 = script data
    }

    /// <summary>
    /// Create AVC decoder configuration record from SPS/PPS NAL units.
    /// Returns the complete FLV tag (video tag type 9, AVC sequence header).
    /// </summary>
    public byte[] CreateDecoderConfigTag(byte[] sps, byte[] pps)
    {
        // Build AVCDecoderConfigurationRecord
        using var config = new MemoryStream(64);
        config.WriteByte(0x01); // configurationVersion
        config.WriteByte(sps.Length > 1 ? sps[1] : (byte)0x64); // AVCProfileIndication
        config.WriteByte(sps.Length > 2 ? sps[2] : (byte)0x00); // profile_compatibility
        config.WriteByte(sps.Length > 3 ? sps[3] : (byte)0x1F); // AVCLevelIndication
        config.WriteByte(0xFF); // 6 bits reserved (111111) + 2 bits NAL length size - 1 (11 = 4 bytes)
        config.WriteByte(0xE1); // 3 bits reserved (111) + 5 bits SPS count (1)
        WriteUInt16BE(config, (ushort)sps.Length);
        config.Write(sps);
        config.WriteByte(0x01); // PPS count
        WriteUInt16BE(config, (ushort)pps.Length);
        config.Write(pps);

        // Build video tag data: frame type (keyframe=1) + codec (AVC=7) + AVC packet type (sequence header=0)
        using var videoData = new MemoryStream((int)config.Length + 5);
        videoData.WriteByte(0x17); // keyframe (1 << 4) | AVC (7)
        videoData.WriteByte(0x00); // AVC sequence header
        videoData.WriteByte(0x00); // composition time offset (3 bytes)
        videoData.WriteByte(0x00);
        videoData.WriteByte(0x00);
        videoData.Write(config.ToArray());

        var tag = CreateTag(9, videoData.ToArray(), 0);
        _decoderConfigTag = tag;
        _hasSentDecoderConfig = true;
        return tag;
    }

    /// <summary>
    /// Mux an AVCC H.264 packet into an FLV video tag.
    /// Filters out SPS/PPS NAL units (types 7, 8) which belong in the decoder config.
    /// Only includes video NALs (IDR type 5, non-IDR type 1, etc.) in the video tag.
    /// </summary>
    /// <param name="avccData">H.264 packet in AVCC format (4-byte length-prefixed NALs)</param>
    /// <param name="isKeyframe">Whether this is a keyframe (IDR)</param>
    /// <param name="nalLengthSize">AVCC NAL unit length prefix size (typically 4)</param>
    /// <returns>FLV video tag bytes, or null if no video NALs found</returns>
    public byte[]? MuxAvccPacket(byte[] avccData, bool isKeyframe, int nalLengthSize = 4)
    {
        // Parse AVCC NALs and filter out SPS/PPS (types 7, 8)
        // Only include video NALs in the FLV video tag
        using var filteredNals = new MemoryStream(avccData.Length);
        int offset = 0;

        while (offset + nalLengthSize <= avccData.Length)
        {
            // Read NAL length (big-endian)
            int nalLength = 0;
            for (int i = 0; i < nalLengthSize; i++)
                nalLength = (nalLength << 8) | avccData[offset + i];
            offset += nalLengthSize;

            if (nalLength <= 0 || offset + nalLength > avccData.Length)
                break;

            // Check NAL type
            int nalType = avccData[offset] & 0x1F;

            // Skip SPS (7) and PPS (8) - they belong in decoder config, not video tags
            if (nalType != 7 && nalType != 8)
            {
                // Write 4-byte length prefix + NAL data
                filteredNals.WriteByte((byte)((nalLength >> 24) & 0xFF));
                filteredNals.WriteByte((byte)((nalLength >> 16) & 0xFF));
                filteredNals.WriteByte((byte)((nalLength >> 8) & 0xFF));
                filteredNals.WriteByte((byte)(nalLength & 0xFF));
                filteredNals.Write(avccData, offset, nalLength);
            }

            offset += nalLength;
        }

        if (filteredNals.Length == 0)
            return null; // No video NALs (only SPS/PPS)

        // Build video tag data
        // Format: FrameType(4 bits) + CodecID(4 bits) + AVCPacketType(1 byte) + CompositionTimeOffset(3 bytes) + Data
        var nalData = filteredNals.ToArray();
        using var videoData = new MemoryStream(nalData.Length + 5);
        videoData.WriteByte(isKeyframe ? (byte)0x17 : (byte)0x27); // frame type + AVC codec
        videoData.WriteByte(0x01); // AVC NALU
        videoData.WriteByte(0x00); // composition time offset (3 bytes)
        videoData.WriteByte(0x00);
        videoData.WriteByte(0x00);
        videoData.Write(nalData);

        var tag = CreateTag(9, videoData.ToArray(), _timestamp);
        _timestamp += _frameDurationMs;
        return tag;
    }

    /// <summary>
    /// Get cached decoder config tag. Returns null if not yet created.
    /// </summary>
    public byte[]? GetDecoderConfigTag() => _decoderConfigTag;

    /// <summary>
    /// Whether decoder config has been sent at least once.
    /// </summary>
    public bool HasDecoderConfig => _hasSentDecoderConfig;

    /// <summary>
    /// Reset timestamp (for new clients that get their own muxer).
    /// </summary>
    public void ResetTimestamp()
    {
        _timestamp = 0;
        _hasSentDecoderConfig = false;
    }

    // === FLV Tag Construction ===

    /// <summary>
    /// Create an FLV tag with header (11 bytes) + data + PreviousTagSize (4 bytes).
    /// </summary>
    private static byte[] CreateTag(byte tagType, byte[] data, int timestamp)
    {
        // Tag: 11 byte header + data + 4 byte PreviousTagSize
        var tag = new byte[11 + data.Length + 4];
        int offset = 0;

        // Tag type (1 byte)
        tag[offset++] = tagType;

        // Data size (3 bytes, big-endian)
        tag[offset++] = (byte)((data.Length >> 16) & 0xFF);
        tag[offset++] = (byte)((data.Length >> 8) & 0xFF);
        tag[offset++] = (byte)(data.Length & 0xFF);

        // Timestamp (3 bytes lower + 1 byte upper, big-endian)
        tag[offset++] = (byte)((timestamp >> 16) & 0xFF);
        tag[offset++] = (byte)((timestamp >> 8) & 0xFF);
        tag[offset++] = (byte)(timestamp & 0xFF);
        tag[offset++] = (byte)((timestamp >> 24) & 0xFF);

        // Stream ID (3 bytes, always 0)
        tag[offset++] = 0;
        tag[offset++] = 0;
        tag[offset++] = 0;

        // Data
        Array.Copy(data, 0, tag, offset, data.Length);
        offset += data.Length;

        // PreviousTagSize (4 bytes) = 11 + data.Length
        int prevTagSize = 11 + data.Length;
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(offset), (uint)prevTagSize);

        return tag;
    }

    // === AMF Helpers ===

    private static void WriteAmfString(Stream s, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteUInt16BE(s, (ushort)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteUInt16BE(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteUInt32BE(Stream s, uint value)
    {
        s.WriteByte((byte)((value >> 24) & 0xFF));
        s.WriteByte((byte)((value >> 16) & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteDoubleBE(Stream s, double value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buf, value);
        s.Write(buf);
    }
}
