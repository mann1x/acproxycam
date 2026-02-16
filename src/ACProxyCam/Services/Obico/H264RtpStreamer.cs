// H264RtpStreamer.cs - Streams H.264 packets from FfmpegDecoder to Janus via RTP

using System.Net;
using System.Net.Sockets;
using ACProxyCam.Services;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Streams H.264 video to Janus via RTP.
/// Uses packets from any IH264PacketSource (decoder or encoder).
/// Implements RFC 6184 for H.264 RTP packetization.
/// </summary>
public class H264RtpStreamer : IDisposable
{
    private readonly IH264PacketSource _packetSource;
    private readonly string _janusServer;
    private readonly int _rtpPort;

    private Socket? _udpSocket;
    private IPEndPoint? _endpoint;

    private volatile bool _isStreaming;
    private volatile bool _isDisposed;

    // RTP state
    private ushort _sequenceNumber;
    private uint _timestamp;
    private uint _ssrc;
    private const int MaxRtpPayloadSize = 1300; // Max payload per RTP packet
    private const byte H264PayloadType = 96; // Dynamic payload type for H.264

    // Stats
    private long _packetsSent;
    private long _bytesSent;
    private DateTime _lastLogTime = DateTime.UtcNow;

    // NAL type tracking for diagnostics
    private int _spsCount;
    private int _ppsCount;
    private int _idrCount;
    private int _nonIdrCount;

    // SPS/PPS from extradata
    private byte[]? _spsNal;
    private byte[]? _ppsNal;
    private bool _spsPpsParsed;

    // Verbose logging for debugging
    public bool Verbose { get; set; }

    public event EventHandler<string>? StatusChanged;

    public bool IsStreaming => _isStreaming;
    public long PacketsSent => _packetsSent;
    public long BytesSent => _bytesSent;

    /// <summary>
    /// Create an H.264 RTP streamer from any H.264 packet source.
    /// </summary>
    /// <param name="packetSource">H.264 packet source (decoder or encoder)</param>
    /// <param name="janusServer">Janus server hostname or IP</param>
    /// <param name="rtpPort">RTP port on Janus for video</param>
    public H264RtpStreamer(IH264PacketSource packetSource, string janusServer, int rtpPort)
    {
        _packetSource = packetSource;
        _janusServer = janusServer;
        _rtpPort = rtpPort;
        _ssrc = (uint)Random.Shared.Next();
    }

    /// <summary>
    /// Start streaming H.264 to Janus.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isStreaming)
            return;

        try
        {
            // Resolve hostname
            IPAddress serverIp;
            if (IPAddress.TryParse(_janusServer, out var parsedIp))
            {
                serverIp = parsedIp;
            }
            else
            {
                var addresses = await Dns.GetHostAddressesAsync(_janusServer);
                serverIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? throw new InvalidOperationException($"Could not resolve Janus server: {_janusServer}");
            }

            _endpoint = new IPEndPoint(serverIp, _rtpPort);

            // Create UDP socket
            _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpSocket.SendBufferSize = 1024 * 1024; // 1MB send buffer

            // Subscribe to raw packets from decoder
            _packetSource.RawPacketReceived += OnRawPacketReceived;

            _isStreaming = true;
            _packetsSent = 0;
            _bytesSent = 0;
            _lastLogTime = DateTime.UtcNow;

            Log($"Started H.264 RTP streaming to {_janusServer}:{_rtpPort}");
        }
        catch (Exception ex)
        {
            Log($"Failed to start RTP streaming: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Stop streaming.
    /// </summary>
    public Task StopAsync()
    {
        if (!_isStreaming)
            return Task.CompletedTask;

        _isStreaming = false;

        // Unsubscribe from decoder
        _packetSource.RawPacketReceived -= OnRawPacketReceived;

        _udpSocket?.Close();
        _udpSocket?.Dispose();
        _udpSocket = null;

        Log("Stopped H.264 RTP streaming");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle raw H.264 packet from decoder.
    /// </summary>
    private void OnRawPacketReceived(object? sender, RawPacketEventArgs e)
    {
        if (!_isStreaming || _udpSocket == null || _endpoint == null)
            return;

        try
        {
            // Parse extradata (SPS/PPS) once if not yet done
            if (!_spsPpsParsed && _packetSource.Extradata != null)
            {
                ParseAvccExtradata(_packetSource.Extradata);
            }

            // Send SPS/PPS before keyframes to ensure decoder can initialize
            if (e.IsKeyframe && _spsNal != null && _ppsNal != null)
            {
                SendSingleNalUnit(_spsNal, false);
                _spsCount++;
                SendSingleNalUnit(_ppsNal, false);
                _ppsCount++;
            }

            // Parse and send NAL units from the packet
            SendH264Packet(e.Data, e.IsKeyframe);

            // Update timestamp (90kHz clock, ~3000 ticks per frame at 30fps)
            _timestamp += 3000;

            // Log stats periodically (only in verbose mode)
            if (Verbose && (DateTime.UtcNow - _lastLogTime).TotalSeconds >= 30)
            {
                var elapsed = (DateTime.UtcNow - _lastLogTime).TotalSeconds;
                var kbps = (_bytesSent * 8.0 / 1000.0) / elapsed;
                Log($"RTP stats: {_packetsSent} packets, {kbps:F1} kbps (SPS:{_spsCount} PPS:{_ppsCount} IDR:{_idrCount} P:{_nonIdrCount})");
                _packetsSent = 0;
                _bytesSent = 0;
                _spsCount = 0;
                _ppsCount = 0;
                _idrCount = 0;
                _nonIdrCount = 0;
                _lastLogTime = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log($"RTP send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Send H.264 data via RTP.
    /// The data may contain one or more NAL units (AVCC or Annex B format).
    /// </summary>
    private void SendH264Packet(byte[] data, bool isKeyframe)
    {
        // Parse NAL units (handles both AVCC and Annex B format)
        var nalUnits = ParseNalUnits(data);

        for (int i = 0; i < nalUnits.Count; i++)
        {
            var nal = nalUnits[i];
            if (nal.Length == 0)
                continue;

            // Track NAL types for diagnostics
            int nalType = nal[0] & 0x1F;
            switch (nalType)
            {
                case 7: _spsCount++; break;  // SPS
                case 8: _ppsCount++; break;  // PPS
                case 5: _idrCount++; break;  // IDR (keyframe)
                case 1: _nonIdrCount++; break; // Non-IDR (P/B frame)
            }

            // Marker bit should only be set on the last NAL unit of the frame
            bool isLastNal = (i == nalUnits.Count - 1);

            if (nal.Length <= MaxRtpPayloadSize)
            {
                // Single NAL unit packet
                SendSingleNalUnit(nal, isLastNal);
            }
            else
            {
                // Fragment NAL unit using FU-A
                SendFragmentedNalUnit(nal, isLastNal);
            }
        }
    }

    /// <summary>
    /// Parse NAL units from packet data.
    /// Handles both AVCC format (4-byte length prefix, from FLV/MP4) and Annex B (start codes).
    /// </summary>
    private static List<byte[]> ParseNalUnits(byte[] data)
    {
        var nalUnits = new List<byte[]>();

        if (data.Length < 4)
        {
            if (data.Length > 0)
                nalUnits.Add(data);
            return nalUnits;
        }

        // Check if this is Annex B format (start codes) or AVCC format (length prefix)
        // Annex B starts with 0x00 0x00 0x01 or 0x00 0x00 0x00 0x01
        bool isAnnexB = (data[0] == 0 && data[1] == 0 && data[2] == 1) ||
                        (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1);

        if (isAnnexB)
        {
            return ParseAnnexBNalUnits(data);
        }
        else
        {
            return ParseAvccNalUnits(data);
        }
    }

    /// <summary>
    /// Parse NAL units from AVCC format (4-byte big-endian length prefix).
    /// This is what FFmpeg outputs when demuxing from FLV/MP4 containers.
    /// </summary>
    private static List<byte[]> ParseAvccNalUnits(byte[] data)
    {
        var nalUnits = new List<byte[]>();
        int offset = 0;

        while (offset + 4 <= data.Length)
        {
            // Read 4-byte big-endian length
            int nalLength = (data[offset] << 24) | (data[offset + 1] << 16) |
                           (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;

            // Sanity check - length should be reasonable
            if (nalLength <= 0 || nalLength > data.Length - offset)
            {
                // Invalid length - might be Annex B after all, or corrupted
                // Try to recover by treating rest as single NAL
                if (offset < data.Length)
                {
                    var remaining = new byte[data.Length - offset + 4];
                    Array.Copy(data, offset - 4, remaining, 0, remaining.Length);
                    nalUnits.Add(remaining);
                }
                break;
            }

            // Extract NAL unit
            var nal = new byte[nalLength];
            Array.Copy(data, offset, nal, 0, nalLength);
            nalUnits.Add(nal);
            offset += nalLength;
        }

        return nalUnits;
    }

    /// <summary>
    /// Parse NAL units from Annex B byte stream (start code separated).
    /// </summary>
    private static List<byte[]> ParseAnnexBNalUnits(byte[] data)
    {
        var nalUnits = new List<byte[]>();
        int i = 0;

        while (i < data.Length)
        {
            // Find start code (0x00 0x00 0x01 or 0x00 0x00 0x00 0x01)
            int startCodeLen = 0;
            if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    startCodeLen = 3;
                }
                else if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLen = 4;
                }
            }

            if (startCodeLen == 0)
            {
                i++;
                continue;
            }

            // Skip start code
            int nalStart = i + startCodeLen;
            i = nalStart;

            // Find next start code or end of data
            int nalEnd = data.Length;
            while (i < data.Length - 2)
            {
                if (data[i] == 0 && data[i + 1] == 0 &&
                    (data[i + 2] == 1 || (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)))
                {
                    nalEnd = i;
                    break;
                }
                i++;
            }

            // Extract NAL unit
            if (nalEnd > nalStart)
            {
                var nal = new byte[nalEnd - nalStart];
                Array.Copy(data, nalStart, nal, 0, nal.Length);
                nalUnits.Add(nal);
            }
        }

        return nalUnits;
    }

    /// <summary>
    /// Send a single NAL unit in one RTP packet.
    /// </summary>
    /// <param name="nal">NAL unit data</param>
    /// <param name="marker">Set marker bit (last NAL of frame)</param>
    private void SendSingleNalUnit(byte[] nal, bool marker)
    {
        // RTP header (12 bytes) + NAL unit
        var rtpPacket = new byte[12 + nal.Length];

        // RTP header
        rtpPacket[0] = 0x80; // Version 2, no padding, no extension, no CSRC
        rtpPacket[1] = (byte)(H264PayloadType | (marker ? 0x80 : 0)); // Marker bit on last NAL
        rtpPacket[2] = (byte)(_sequenceNumber >> 8);
        rtpPacket[3] = (byte)(_sequenceNumber & 0xFF);
        rtpPacket[4] = (byte)((_timestamp >> 24) & 0xFF);
        rtpPacket[5] = (byte)((_timestamp >> 16) & 0xFF);
        rtpPacket[6] = (byte)((_timestamp >> 8) & 0xFF);
        rtpPacket[7] = (byte)(_timestamp & 0xFF);
        rtpPacket[8] = (byte)((_ssrc >> 24) & 0xFF);
        rtpPacket[9] = (byte)((_ssrc >> 16) & 0xFF);
        rtpPacket[10] = (byte)((_ssrc >> 8) & 0xFF);
        rtpPacket[11] = (byte)(_ssrc & 0xFF);

        // NAL unit data
        Array.Copy(nal, 0, rtpPacket, 12, nal.Length);

        SendRtpPacket(rtpPacket);
        _sequenceNumber++;
    }

    /// <summary>
    /// Send a NAL unit fragmented using FU-A (RFC 6184).
    /// </summary>
    /// <param name="nal">NAL unit data</param>
    /// <param name="marker">Set marker bit on last fragment (if this is last NAL of frame)</param>
    private void SendFragmentedNalUnit(byte[] nal, bool marker)
    {
        // NAL unit header is first byte
        byte nalHeader = nal[0];
        byte nalType = (byte)(nalHeader & 0x1F);
        byte nri = (byte)(nalHeader & 0x60);

        // FU indicator: type 28 (FU-A)
        byte fuIndicator = (byte)(nri | 28);

        int offset = 1; // Skip NAL header
        bool isFirst = true;

        while (offset < nal.Length)
        {
            int payloadSize = Math.Min(MaxRtpPayloadSize - 2, nal.Length - offset); // -2 for FU header
            bool isLastFragment = (offset + payloadSize >= nal.Length);

            // RTP header (12 bytes) + FU indicator (1 byte) + FU header (1 byte) + payload
            var rtpPacket = new byte[12 + 2 + payloadSize];

            // RTP header - marker bit only on last fragment of last NAL
            rtpPacket[0] = 0x80;
            rtpPacket[1] = (byte)(H264PayloadType | ((isLastFragment && marker) ? 0x80 : 0));
            rtpPacket[2] = (byte)(_sequenceNumber >> 8);
            rtpPacket[3] = (byte)(_sequenceNumber & 0xFF);
            rtpPacket[4] = (byte)((_timestamp >> 24) & 0xFF);
            rtpPacket[5] = (byte)((_timestamp >> 16) & 0xFF);
            rtpPacket[6] = (byte)((_timestamp >> 8) & 0xFF);
            rtpPacket[7] = (byte)(_timestamp & 0xFF);
            rtpPacket[8] = (byte)((_ssrc >> 24) & 0xFF);
            rtpPacket[9] = (byte)((_ssrc >> 16) & 0xFF);
            rtpPacket[10] = (byte)((_ssrc >> 8) & 0xFF);
            rtpPacket[11] = (byte)(_ssrc & 0xFF);

            // FU indicator
            rtpPacket[12] = fuIndicator;

            // FU header: S=start, E=end, R=0, Type=NAL type
            byte fuHeader = nalType;
            if (isFirst) fuHeader |= 0x80; // Start bit
            if (isLastFragment) fuHeader |= 0x40;  // End bit
            rtpPacket[13] = fuHeader;

            // Payload
            Array.Copy(nal, offset, rtpPacket, 14, payloadSize);

            SendRtpPacket(rtpPacket);
            _sequenceNumber++;

            offset += payloadSize;
            isFirst = false;
        }
    }

    /// <summary>
    /// Send an RTP packet via UDP.
    /// </summary>
    private void SendRtpPacket(byte[] packet)
    {
        if (_udpSocket == null || _endpoint == null)
            return;

        try
        {
            _udpSocket.SendTo(packet, _endpoint);
            _packetsSent++;
            _bytesSent += packet.Length;
        }
        catch (SocketException)
        {
            // Ignore transient socket errors
        }
    }

    /// <summary>
    /// Parse SPS and PPS from extradata (auto-detects AVCC or Annex B format).
    /// AVCC: config record with length-prefixed NALs (from FLV/MP4 decoders).
    /// Annex B: start-code separated NALs (from libx264 encoder with GLOBAL_HEADER).
    /// </summary>
    private void ParseAvccExtradata(byte[] extradata)
    {
        if (extradata == null || extradata.Length < 4)
            return;

        try
        {
            // Detect format: Annex B starts with 00 00 00 01 or 00 00 01
            bool isAnnexB = (extradata[0] == 0 && extradata[1] == 0 &&
                            (extradata[2] == 1 || (extradata.Length >= 5 && extradata[2] == 0 && extradata[3] == 1)));

            if (isAnnexB)
                ParseAnnexBExtradata(extradata);
            else
                ParseAvccFormatExtradata(extradata);
        }
        catch (Exception ex)
        {
            Log($"Failed to parse extradata: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse SPS/PPS from Annex B format extradata (start-code separated NAL units).
    /// libx264 with AV_CODEC_FLAG_GLOBAL_HEADER produces this format.
    /// </summary>
    private void ParseAnnexBExtradata(byte[] extradata)
    {
        int i = 0;
        while (i < extradata.Length)
        {
            int startCodeLen = 0;
            if (i + 3 <= extradata.Length && extradata[i] == 0 && extradata[i + 1] == 0 && extradata[i + 2] == 1)
                startCodeLen = 3;
            else if (i + 4 <= extradata.Length && extradata[i] == 0 && extradata[i + 1] == 0 && extradata[i + 2] == 0 && extradata[i + 3] == 1)
                startCodeLen = 4;
            else { i++; continue; }

            int nalStart = i + startCodeLen;

            // Find next start code or end of data
            int nalEnd = extradata.Length;
            for (int j = nalStart + 1; j < extradata.Length - 2; j++)
            {
                if (extradata[j] == 0 && extradata[j + 1] == 0 &&
                    (extradata[j + 2] == 1 || (j + 3 < extradata.Length && extradata[j + 2] == 0 && extradata[j + 3] == 1)))
                { nalEnd = j; break; }
            }

            int nalLength = nalEnd - nalStart;
            if (nalLength > 0)
            {
                byte nalType = (byte)(extradata[nalStart] & 0x1F);
                if (nalType == 7 && _spsNal == null) // SPS
                {
                    _spsNal = new byte[nalLength];
                    Array.Copy(extradata, nalStart, _spsNal, 0, nalLength);
                    Log($"Extracted SPS from Annex B extradata: {nalLength} bytes");
                }
                else if (nalType == 8 && _ppsNal == null) // PPS
                {
                    _ppsNal = new byte[nalLength];
                    Array.Copy(extradata, nalStart, _ppsNal, 0, nalLength);
                    Log($"Extracted PPS from Annex B extradata: {nalLength} bytes");
                }
            }

            i = nalEnd;
        }

        _spsPpsParsed = _spsNal != null && _ppsNal != null;
        if (!_spsPpsParsed)
            Log($"Warning: Annex B extradata missing SPS or PPS (sps={_spsNal != null}, pps={_ppsNal != null})");
    }

    /// <summary>
    /// Parse SPS and PPS from AVCC extradata format.
    /// AVCC format: configuration version, profile, compatibility, level, NAL length size,
    /// then SPS count, SPS data, PPS count, PPS data.
    /// </summary>
    private void ParseAvccFormatExtradata(byte[] extradata)
    {
        if (extradata.Length < 8)
            return;

        int offset = 5; // Skip config header

        // Number of SPS (lower 5 bits)
        int numSps = extradata[offset] & 0x1F;
        offset++;

        for (int i = 0; i < numSps && offset + 2 <= extradata.Length; i++)
        {
            int spsLen = (extradata[offset] << 8) | extradata[offset + 1];
            offset += 2;
            if (offset + spsLen <= extradata.Length)
            {
                _spsNal = new byte[spsLen];
                Array.Copy(extradata, offset, _spsNal, 0, spsLen);
                offset += spsLen;
                Log($"Extracted SPS from AVCC extradata: {spsLen} bytes");
            }
        }

        // Number of PPS
        if (offset < extradata.Length)
        {
            int numPps = extradata[offset];
            offset++;
            for (int i = 0; i < numPps && offset + 2 <= extradata.Length; i++)
            {
                int ppsLen = (extradata[offset] << 8) | extradata[offset + 1];
                offset += 2;
                if (offset + ppsLen <= extradata.Length)
                {
                    _ppsNal = new byte[ppsLen];
                    Array.Copy(extradata, offset, _ppsNal, 0, ppsLen);
                    offset += ppsLen;
                    Log($"Extracted PPS from AVCC extradata: {ppsLen} bytes");
                }
            }
        }

        _spsPpsParsed = true;
    }

    private void Log(string message)
    {
        StatusChanged?.Invoke(this, $"[H264RTP] {message}");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _isStreaming = false;
        _packetSource.RawPacketReceived -= OnRawPacketReceived;
        _udpSocket?.Close();
        _udpSocket?.Dispose();
    }
}
