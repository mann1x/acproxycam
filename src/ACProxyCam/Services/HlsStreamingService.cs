// HlsStreamingService.cs - Memory-efficient HLS streaming service with LL-HLS support
// Manages HLS segment generation from H.264 NAL units with minimal allocations
// Supports Low-Latency HLS (LL-HLS) with partial segments for ~1-2s latency

using System.Buffers;
using System.Text;

namespace ACProxyCam.Services;

/// <summary>
/// Memory-efficient HLS streaming service that generates MPEG-TS segments from H.264 streams.
/// Supports LL-HLS (Low-Latency HLS) with partial segments for reduced latency.
/// Uses ArrayPool and pre-allocated buffers to minimize GC pressure.
/// </summary>
public class HlsStreamingService : IDisposable
{
    // Standard HLS configuration
    private const int DefaultSegmentDurationMs = 800; // ~0.8 seconds per segment
    private const int MaxSegmentSize = 4 * 1024 * 1024;  // 4MB max per segment
    private const int SegmentExpiryMs = 30000;       // Expire segments 30s after leaving window

    // LL-HLS configuration
    private const int DefaultPartDurationMs = 200;    // Apple recommended: 200ms parts
    private const int DefaultPartsPerSegment = 4;     // ~800ms segment (4 x 200ms)
    private const int PartHoldBackParts = 4;          // PART-HOLD-BACK = 4 parts (~0.8s) for low latency
    private const int MaxPartsPerSegment = 10;        // Safety limit
    private const int MaxPartSize = 1024 * 1024;      // 1MB max per part

    // Configurable settings
    private readonly int _bufferWindowSeconds;
    private readonly int _maxSegments;
    private bool _llHlsEnabled = true;
    private int _partDurationMs = DefaultPartDurationMs;
    private int _partsPerSegment = DefaultPartsPerSegment;
    private int _targetSegmentDurationMs = DefaultSegmentDurationMs;

    // Session ID for cache-busting segment URLs
    private readonly string _sessionId = Random.Shared.Next(10000000, 99999999).ToString();

    // Legacy HLS: Track cumulative PTS offset from evicted segments
    // This is subtracted from segment PTS to make timeline match playlist
    private long _legacyPtsOffset90k = 0;

    // Segment storage - use fixed-size pooled buffers
    private readonly HlsSegmentBuffer[] _segmentBuffers;
    private readonly object _segmentLock = new();
    private int _currentSegmentIndex;
    private int _oldestSegmentIndex;
    private int _mediaSequence;
    private int _segmentCount;

    // Current segment being built
    private readonly MpegTsMuxer _muxer = new();
    private MemoryStream? _currentStream;
    private DateTime _segmentStartTime;
    private bool _waitingForKeyframe = true;
    private bool _hasData;
    private bool _segmentNeedsPatPmt = true;
    private bool _segmentNeedsDiscontinuity = true;
    private bool _segmentNeedsSpsPps = true;

    // LL-HLS partial segment tracking
    private readonly List<HlsPartialSegment> _currentParts = new();
    private long _currentPartStartPos = -1;  // Position in _currentStream where current part starts
    private DateTime _partStartTime;
    private bool _currentPartHasKeyframe;
    private int _currentPartIndex;
    private int _currentPartFrameCount;  // Frames in current part for accurate duration
    private int _currentSegmentFrameCount;  // Frames in current segment for accurate duration
    private long _currentPartStartPts = -1;  // PTS at start of current part for accurate duration
    private long _currentSegmentStartPts = -1;  // PTS at start of current segment
    private long _lastFramePts = 0;  // PTS of last frame written

    // LL-HLS blocking request support
    private readonly Dictionary<(int Msn, int PartIndex), TaskCompletionSource<bool>> _partWaiters = new();
    private readonly object _waiterLock = new();
    private int _latestCompleteMsn = -1;
    private int _latestCompletePartIndex = -1;

    // H.264 parameters
    private byte[]? _spsNal;
    private byte[]? _ppsNal;
    private int _nalLengthSize = 4;

    // Frame rate tracking
    private double _estimatedFps = 25.0;
    private DateTime _lastFrameTime;

    // Wall-clock based PTS for accurate real-world latency
    // Using wall-clock ensures the HLS timeline matches real time
    private DateTime _streamStartWallClock;
    private bool _wallClockInitialized;

    // Diagnostic counters
    private int _totalPacketsReceived;
    private int _keyframesReceived;
    private int _nonKeyframesReceived;
    private int _framesWritten;
    private int _partsEmitted;
    private int _skippedWaitingKeyframe;
    private int _skippedNoStream;
    private int _skippedSmallPacket;
    private DateTime _lastDiagLog = DateTime.MinValue;

    private bool _disposed;

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Create HLS streaming service with configurable buffer window.
    /// </summary>
    /// <param name="bufferWindowSeconds">Buffer window in seconds (2-60, default 10)</param>
    public HlsStreamingService(int bufferWindowSeconds = 10)
    {
        // Clamp buffer window to reasonable range - keep short for low latency
        _bufferWindowSeconds = Math.Clamp(bufferWindowSeconds, 2, 60);

        // Calculate max segments: window / segment_duration + 1 for current
        _maxSegments = (_bufferWindowSeconds * 1000 / _targetSegmentDurationMs) + 1;

        _segmentBuffers = new HlsSegmentBuffer[_maxSegments];
        for (int i = 0; i < _maxSegments; i++)
        {
            _segmentBuffers[i] = new HlsSegmentBuffer(MaxSegmentSize);
        }
    }

    /// <summary>
    /// Configure LL-HLS settings.
    /// </summary>
    /// <param name="enabled">Enable LL-HLS partial segments</param>
    /// <param name="partDurationMs">Part duration in milliseconds (100-500)</param>
    public void ConfigureLlHls(bool enabled, int partDurationMs = DefaultPartDurationMs)
    {
        _llHlsEnabled = enabled;
        _partDurationMs = Math.Clamp(partDurationMs, 100, 500);
        _partsPerSegment = Math.Clamp(_targetSegmentDurationMs / _partDurationMs, 2, MaxPartsPerSegment);

        StatusChanged?.Invoke(this, $"LL-HLS {(enabled ? "enabled" : "disabled")}: part={_partDurationMs}ms, parts/segment={_partsPerSegment}");
    }

    /// <summary>
    /// Whether HLS streaming is ready (has at least one complete segment).
    /// </summary>
    public bool IsReady => _segmentCount > 0;

    /// <summary>
    /// Whether LL-HLS is enabled.
    /// </summary>
    public bool LlHlsEnabled => _llHlsEnabled;

    /// <summary>
    /// Session ID for cache-busting segment URLs.
    /// </summary>
    public string SessionId => _sessionId;

    /// <summary>
    /// Current number of available segments.
    /// </summary>
    public int SegmentCount => _segmentCount;

    /// <summary>
    /// Configured buffer window in seconds.
    /// </summary>
    public int BufferWindowSeconds => _bufferWindowSeconds;

    /// <summary>
    /// Current estimated FPS from input stream.
    /// </summary>
    public double EstimatedFps => _estimatedFps;

    /// <summary>
    /// Get the latest media sequence number that has at least one part available.
    /// </summary>
    public int GetLatestMediaSequence()
    {
        lock (_segmentLock)
        {
            // If we have parts in the current segment being built, that's the latest
            if (_currentParts.Count > 0)
            {
                return _mediaSequence + _segmentCount;
            }
            // Otherwise, use the last complete segment
            return _mediaSequence + _segmentCount - 1;
        }
    }

    /// <summary>
    /// Get the latest part index for the given media sequence number.
    /// Returns -1 if no parts available for that MSN.
    /// </summary>
    public int GetLatestPartIndex(int msn)
    {
        lock (_segmentLock)
        {
            int currentMsn = _mediaSequence + _segmentCount;

            if (msn == currentMsn && _currentParts.Count > 0)
            {
                return _currentParts.Count - 1;
            }

            // Check complete segments
            for (int i = 0; i < _segmentCount; i++)
            {
                int idx = (_oldestSegmentIndex + i) % _maxSegments;
                var seg = _segmentBuffers[idx];
                if (seg.IsValid && seg.SequenceNumber == msn)
                {
                    return seg.PartCount - 1;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Wait for a specific part to become available.
    /// Used for LL-HLS blocking playlist requests.
    /// </summary>
    /// <param name="msn">Media sequence number</param>
    /// <param name="partIndex">Part index within the segment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if part is available, false if timed out or cancelled</returns>
    public async Task<bool> WaitForPartAsync(int msn, int partIndex, CancellationToken cancellationToken)
    {
        // Check if part is already available
        if (IsPartAvailable(msn, partIndex))
        {
            return true;
        }

        // Create a waiter
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_waiterLock)
        {
            var key = (msn, partIndex);
            if (!_partWaiters.ContainsKey(key))
            {
                _partWaiters[key] = tcs;
            }
            else
            {
                // Already waiting for this part
                tcs = _partWaiters[key];
            }
        }

        // Register cancellation
        using var registration = cancellationToken.Register(() => tcs.TrySetResult(false));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_waiterLock)
            {
                _partWaiters.Remove((msn, partIndex));
            }
        }
    }

    /// <summary>
    /// Check if a specific part is available.
    /// </summary>
    private bool IsPartAvailable(int msn, int partIndex)
    {
        lock (_segmentLock)
        {
            int currentMsn = _mediaSequence + _segmentCount;

            // Check current segment being built
            if (msn == currentMsn && partIndex < _currentParts.Count)
            {
                return true;
            }

            // Check complete segments
            for (int i = 0; i < _segmentCount; i++)
            {
                int idx = (_oldestSegmentIndex + i) % _maxSegments;
                var seg = _segmentBuffers[idx];
                if (seg.IsValid && seg.SequenceNumber == msn && partIndex < seg.PartCount)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Notify waiters that a part is available.
    /// </summary>
    private void NotifyPartWaiters(int msn, int partIndex)
    {
        lock (_waiterLock)
        {
            // Notify exact match
            if (_partWaiters.TryGetValue((msn, partIndex), out var tcs))
            {
                tcs.TrySetResult(true);
            }

            // Also notify waiters for earlier parts (they should be satisfied)
            var toNotify = _partWaiters
                .Where(kvp => kvp.Key.Msn < msn || (kvp.Key.Msn == msn && kvp.Key.PartIndex <= partIndex))
                .ToList();

            foreach (var kvp in toNotify)
            {
                kvp.Value.TrySetResult(true);
            }

            _latestCompleteMsn = msn;
            _latestCompletePartIndex = partIndex;
        }
    }

    /// <summary>
    /// Get a partial segment by MSN and part index.
    /// </summary>
    public HlsPartialSegment? GetPart(int msn, int partIndex)
    {
        lock (_segmentLock)
        {
            int currentMsn = _mediaSequence + _segmentCount;

            // Check current segment being built
            if (msn == currentMsn && partIndex < _currentParts.Count)
            {
                return _currentParts[partIndex];
            }

            // Check complete segments
            for (int i = 0; i < _segmentCount; i++)
            {
                int idx = (_oldestSegmentIndex + i) % _maxSegments;
                var seg = _segmentBuffers[idx];
                if (seg.IsValid && seg.SequenceNumber == msn)
                {
                    return seg.GetPart(partIndex);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Set H.264 SPS/PPS NAL units from decoder extradata.
    /// </summary>
    public void SetH264Parameters(byte[]? sps, byte[]? pps, int nalLengthSize = 4)
    {
        _spsNal = sps;
        _ppsNal = pps;
        _nalLengthSize = nalLengthSize >= 1 && nalLengthSize <= 4 ? nalLengthSize : 4;
        _muxer.SetH264Parameters(sps, pps);
    }

    /// <summary>
    /// Push an H.264 packet to the HLS stream.
    /// Packets are in AVCC format (length-prefixed NAL units).
    /// </summary>
    public void PushH264Packet(byte[] data, bool isKeyframe, long pts)
    {
        if (_disposed) return;

        if (data.Length < _nalLengthSize)
        {
            _skippedSmallPacket++;
            return;
        }

        try
        {
            // Track all packets
            _totalPacketsReceived++;
            if (isKeyframe) _keyframesReceived++;
            else _nonKeyframesReceived++;

            // Update frame rate estimate from input stream
            UpdateFrameRateEstimate();

            lock (_segmentLock)
            {
                // Log diagnostics every 60 seconds (reduced frequency)
                if ((DateTime.UtcNow - _lastDiagLog).TotalSeconds >= 60)
                {
                    var llhlsInfo = _llHlsEnabled ? $", parts={_partsEmitted}" : "";
                    StatusChanged?.Invoke(this, $"HLS stats: received={_totalPacketsReceived}, written={_framesWritten}, segments={_segmentCount}, fps={_estimatedFps:F1}{llhlsInfo}");
                    _totalPacketsReceived = 0;
                    _keyframesReceived = 0;
                    _nonKeyframesReceived = 0;
                    _framesWritten = 0;
                    _partsEmitted = 0;
                    _skippedWaitingKeyframe = 0;
                    _skippedNoStream = 0;
                    _skippedSmallPacket = 0;
                    _lastDiagLog = DateTime.UtcNow;
                }

                // Expire old segments periodically
                ExpireOldSegments();

                // Wait for first keyframe
                if (_waitingForKeyframe)
                {
                    if (!isKeyframe)
                    {
                        _skippedWaitingKeyframe++;
                        return;
                    }
                    _waitingForKeyframe = false;
                    StartNewSegment();
                    StatusChanged?.Invoke(this, $"HLS: First keyframe received at {_estimatedFps:F1}fps, buffer={_bufferWindowSeconds}s, LL-HLS={_llHlsEnabled}");
                }

                // Check if we should start a new segment
                if (_hasData)
                {
                    var elapsed = (DateTime.UtcNow - _segmentStartTime).TotalMilliseconds;
                    if (elapsed >= _targetSegmentDurationMs)
                    {
                        FinalizeCurrentSegment();
                        StartNewSegment();
                    }
                }

                // Write frame directly to current segment stream
                if (_currentStream == null)
                {
                    _skippedNoStream++;
                    if (_skippedNoStream <= 3)
                    {
                        StatusChanged?.Invoke(this, $"HLS: No stream! waitingKey={_waitingForKeyframe}, hasData={_hasData}, isKey={isKeyframe}");
                    }
                }
                else
                {
                    // Use source PTS for muxer (correct frame timing) and duration (must match)
                    // Track wall-clock offset for diagnostics
                    if (!_wallClockInitialized)
                    {
                        _streamStartWallClock = DateTime.UtcNow;
                        _wallClockInitialized = true;
                        // Log first frame timing for latency analysis
                        StatusChanged?.Invoke(this, $"HLS: First frame, sourcePTS={pts}ms");
                    }

                    // Increment counts BEFORE writing so they're accurate when we check for part finalization
                    _framesWritten++;
                    _currentPartFrameCount++;
                    _currentSegmentFrameCount++;

                    WriteNalUnitsToSegment(data, isKeyframe, pts);
                    _hasData = true;
                }
            }
        }
        catch
        {
            // Silently ignore errors to not affect main stream
        }
    }

    /// <summary>
    /// Parse NAL units from AVCC data and write directly to segment.
    /// For LL-HLS, we track byte positions to extract partial segments from the main stream.
    /// </summary>
    private void WriteNalUnitsToSegment(byte[] data, bool isKeyframe, long sourcePts)
    {
        int offset = 0;
        bool isFirst = true;
        long streamPosBefore = _currentStream!.Position;

        // Force PAT/PMT, discontinuity, and SPS/PPS at segment start
        bool writePatPmt = _segmentNeedsPatPmt;
        bool discontinuity = _segmentNeedsDiscontinuity;
        bool forceSpsPps = _segmentNeedsSpsPps && !isKeyframe;

        // Track keyframes for LL-HLS parts
        if (isKeyframe)
        {
            _currentPartHasKeyframe = true;
        }

        // Track source PTS for accurate duration calculation
        // Source PTS is in milliseconds from the decoder
        if (sourcePts > 0)
        {
            _lastFramePts = sourcePts;
            // Initialize part start PTS on first frame
            if (_currentPartStartPts < 0)
            {
                _currentPartStartPts = sourcePts;
            }
            // Initialize segment start PTS and TIME on first frame
            if (_currentSegmentStartPts < 0)
            {
                _currentSegmentStartPts = sourcePts;
                _segmentStartTime = DateTime.UtcNow;  // Reset segment timer to first frame arrival
            }
        }

        // Track part start position and TIME for LL-HLS
        // Important: Start the timer only when first frame arrives, not when part is created
        if (_llHlsEnabled && _currentPartStartPos < 0)
        {
            _currentPartStartPos = _currentStream.Position;
            _partStartTime = DateTime.UtcNow;  // Reset timer to now (first frame arrival)
        }

        while (offset + _nalLengthSize <= data.Length)
        {
            // Read NAL length
            int nalLength = 0;
            for (int i = 0; i < _nalLengthSize; i++)
            {
                nalLength = (nalLength << 8) | data[offset + i];
            }
            offset += _nalLengthSize;

            if (nalLength <= 0 || nalLength > data.Length - offset)
            {
                if (nalLength > 100000)
                {
                    StatusChanged?.Invoke(this, $"HLS: Suspicious NAL len={nalLength}, dataLen={data.Length}, offset={offset}, nalSize={_nalLengthSize}");
                }
                break;
            }

            // Write frame using source PTS for accurate real-time latency
            // Source PTS represents actual frame capture time from the camera
            var nalSpan = data.AsSpan(offset, nalLength);
            _muxer.WriteFrame(_currentStream!, nalSpan, isKeyframe && isFirst, writePatPmt || isFirst, sourcePts, discontinuity, isFirst, forceSpsPps && isFirst);

            offset += nalLength;
            isFirst = false;
            writePatPmt = false;
            discontinuity = false;
            forceSpsPps = false;
        }

        _segmentNeedsPatPmt = false;
        _segmentNeedsDiscontinuity = false;
        _segmentNeedsSpsPps = false;

        // Check if we should finalize current part (LL-HLS)
        // Use simple frame count for predictable part boundaries
        // At typical frame rates: 2 frames @ 15fps = 133ms, 2 frames @ 4fps = 500ms
        if (_llHlsEnabled && _currentPartStartPos >= 0 && _currentStream.Position > _currentPartStartPos)
        {
            // Finalize part after minimum frames to ensure valid duration
            // Target ~200-300ms parts: 3 frames @ 15fps = 200ms, 2 frames @ 4fps = 500ms
            int minFramesPerPart = Math.Max(2, (int)(_estimatedFps * _partDurationMs / 1000.0));
            if (minFramesPerPart < 2) minFramesPerPart = 2;
            if (minFramesPerPart > 5) minFramesPerPart = 5;

            if (_currentPartFrameCount >= minFramesPerPart)
            {
                FinalizeCurrentPart();
                StartNewPart();
            }
        }

        // Log data size written (only periodically)
        long bytesWritten = _currentStream!.Position - streamPosBefore;
        if (bytesWritten > 50000 || (_framesWritten % 100 == 0))
        {
            StatusChanged?.Invoke(this, $"HLS: Frame written: input={data.Length}B, output={bytesWritten}B, segSize={_currentStream.Length / 1024}KB, key={isKeyframe}");
        }
    }

    /// <summary>
    /// Start a new partial segment for LL-HLS.
    /// Tracks position in the main segment stream.
    /// Note: _currentPartStartPos is set to -1 here, and actual start position + time
    /// are captured when the first frame arrives in WriteNalUnitsToSegment().
    /// </summary>
    private void StartNewPart()
    {
        _currentPartStartPos = -1;  // Will be set when first frame arrives
        _partStartTime = DateTime.UtcNow;  // Placeholder, will be reset on first frame
        _currentPartHasKeyframe = false;
        _currentPartFrameCount = 0;
        _currentPartStartPts = -1;  // Will be set when first frame arrives
    }

    /// <summary>
    /// Finalize the current partial segment by extracting data from the main segment stream.
    /// This ensures continuity counters stay in sync.
    /// </summary>
    private void FinalizeCurrentPart()
    {
        if (_currentStream == null || _currentPartStartPos < 0)
            return;

        long endPos = _currentStream.Position;
        int partLength = (int)(endPos - _currentPartStartPos);

        if (partLength <= 0)
            return;

        // Calculate duration from frame count
        // CRITICAL: Duration = frameCount * frameDuration = frameCount / fps
        // This ensures consecutive parts don't overlap and sum to segment duration
        // Using PTS delta + frame_duration causes overcounting between consecutive parts
        double duration;

        if (_currentPartFrameCount > 0 && _estimatedFps > 0)
        {
            // N frames have total duration of N * frame_duration
            duration = _currentPartFrameCount / _estimatedFps;
        }
        else if (_currentPartStartPts > 0 && _lastFramePts > _currentPartStartPts)
        {
            // Fallback: use PTS delta (doesn't include last frame, but better than nothing)
            duration = (_lastFramePts - _currentPartStartPts) / 1000.0;
        }
        else
        {
            // Minimum sensible duration
            duration = 0.1;
        }
        // Clamp to reasonable range
        if (duration < 0.05) duration = 0.05;
        if (duration > 2.0) duration = 2.0;

        // Log parts for debugging timing issues
        if (_partsEmitted < 10 || _partsEmitted % 50 == 0)
        {
            var ptsDelta = _lastFramePts - _currentPartStartPts;
            StatusChanged?.Invoke(this, $"LL-HLS part {_currentPartIndex}: dur={duration:F3}s, frames={_currentPartFrameCount}, pts_delta={ptsDelta}ms, fps={_estimatedFps:F1}");
        }

        // Extract part data from the segment stream buffer
        var buffer = _currentStream.GetBuffer();
        var partData = new byte[partLength];
        Array.Copy(buffer, _currentPartStartPos, partData, 0, partLength);

        var part = new HlsPartialSegment
        {
            PartIndex = _currentPartIndex,
            Data = partData,
            Duration = duration,
            Independent = _currentPartHasKeyframe,
            CreatedAt = DateTime.UtcNow
        };

        _currentParts.Add(part);
        _currentPartIndex++;
        _partsEmitted++;

        // Notify waiters
        int currentMsn = _mediaSequence + _segmentCount;
        NotifyPartWaiters(currentMsn, part.PartIndex);
    }

    /// <summary>
    /// Expire segments that are outside the buffer window + expiry grace period.
    /// </summary>
    private void ExpireOldSegments()
    {
        var now = DateTime.UtcNow;
        var expiryTime = TimeSpan.FromMilliseconds(_bufferWindowSeconds * 1000 + SegmentExpiryMs);

        for (int i = 0; i < _maxSegments; i++)
        {
            var seg = _segmentBuffers[i];
            if (seg.IsValid && seg.CreatedAt != default)
            {
                if (now - seg.CreatedAt > expiryTime)
                {
                    seg.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Get the M3U8 playlist content.
    /// </summary>
    /// <param name="requestedMsn">Requested MSN for blocking request (-1 for non-blocking)</param>
    /// <param name="requestedPart">Requested part index for blocking request (-1 for non-blocking)</param>
    public string GetPlaylist(int requestedMsn = -1, int requestedPart = -1)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("#EXTM3U");

        lock (_segmentLock)
        {
            // Use version 6 for both LL-HLS and standard HLS
            // Version 6 is widely supported (VLC 3.x+, all major players)
            // LL-HLS extension tags (EXT-X-PART, EXT-X-PRELOAD-HINT, etc.) will be
            // ignored by clients that don't understand them, per HLS spec
            // Note: Version 10 causes VLC and other legacy players to reject the playlist
            sb.AppendLine("#EXT-X-VERSION:6");

            // Target duration must be >= longest segment (rounded up)
            sb.AppendLine($"#EXT-X-TARGETDURATION:{Math.Max(1, (_targetSegmentDurationMs + 999) / 1000)}");

            if (_llHlsEnabled)
            {
                // LL-HLS specific tags (legacy players like VLC will ignore these per HLS spec)
                double partTarget = _partDurationMs / 1000.0;
                double partHoldBack = partTarget * PartHoldBackParts;
                double holdBack = (_targetSegmentDurationMs / 1000.0) * 3;

                sb.AppendLine($"#EXT-X-PART-INF:PART-TARGET={partTarget:F3}");
                sb.AppendLine($"#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK={partHoldBack:F3},HOLD-BACK={holdBack:F3}");
            }
            // Note: EXT-X-START removed - it confuses VLC's clock synchronization

            if (_segmentCount == 0 && _currentParts.Count == 0)
            {
                return sb.ToString();
            }

            // Skip the oldest 2 segments to prevent race condition where segment is evicted
            // between playlist generation and client fetch (causes fragGap errors)
            int skipOldest = Math.Min(2, _segmentCount - 1);
            int adjustedMediaSequence = _mediaSequence + skipOldest;

            sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{adjustedMediaSequence}");

            // Output complete segments with their parts (skip oldest to prevent 404s)
            for (int i = skipOldest; i < _segmentCount; i++)
            {
                int idx = (_oldestSegmentIndex + i) % _maxSegments;
                var seg = _segmentBuffers[idx];
                if (seg.IsValid)
                {
                    // Note: PROGRAM-DATE-TIME removed - it caused fragGap errors in hls.js
                    // and didn't help VLC sync properly with continuous PTS

                    // Add partial segments if LL-HLS enabled
                    if (_llHlsEnabled)
                    {
                        for (int p = 0; p < seg.PartCount; p++)
                        {
                            var part = seg.GetPart(p);
                            if (part != null)
                            {
                                var independent = part.Independent ? ",INDEPENDENT=YES" : "";
                                sb.AppendLine($"#EXT-X-PART:DURATION={part.Duration:F3},URI=\"part-{_sessionId}-{seg.SequenceNumber}.{p}.ts\"{independent}");
                            }
                        }
                    }

                    // Full segment
                    sb.AppendLine($"#EXTINF:{seg.Duration:F3},");
                    sb.AppendLine($"segment-{_sessionId}-{seg.SequenceNumber}.ts");
                }
            }

            // Add parts from current segment being built (LL-HLS only)
            if (_llHlsEnabled && _currentParts.Count > 0)
            {
                int currentMsn = _mediaSequence + _segmentCount;

                for (int p = 0; p < _currentParts.Count; p++)
                {
                    var part = _currentParts[p];
                    var independent = part.Independent ? ",INDEPENDENT=YES" : "";
                    sb.AppendLine($"#EXT-X-PART:DURATION={part.Duration:F3},URI=\"part-{_sessionId}-{currentMsn}.{p}.ts\"{independent}");
                }

                // Add preload hint for next expected part
                int nextPartIndex = _currentParts.Count;
                sb.AppendLine($"#EXT-X-PRELOAD-HINT:TYPE=PART,URI=\"part-{_sessionId}-{currentMsn}.{nextPartIndex}.ts\"");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get a legacy M3U8 playlist for VLC and other players that don't support LL-HLS.
    /// Uses absolute segment sequence numbers for stable identification.
    /// PTS in segments is adjusted to be window-relative (starting near 0).
    /// </summary>
    public string GetLegacyPlaylist()
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("#EXTM3U");

        lock (_segmentLock)
        {
            // Use version 3 for maximum compatibility with legacy players
            sb.AppendLine("#EXT-X-VERSION:3");

            // Target duration must be >= longest segment (rounded up)
            sb.AppendLine($"#EXT-X-TARGETDURATION:{Math.Max(1, (_targetSegmentDurationMs + 999) / 1000)}");

            if (_segmentCount == 0)
            {
                return sb.ToString();
            }

            // Skip the oldest 2 segments to prevent race condition where segment is evicted
            // between playlist generation and client fetch (like the LL-HLS playlist does)
            int skipOldest = Math.Min(2, _segmentCount - 1);
            int adjustedMediaSequence = _mediaSequence + skipOldest;

            sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{adjustedMediaSequence}");
            // DISCONTINUITY-SEQUENCE helps VLC understand that PTS has been reset
            // This should match MEDIA-SEQUENCE so VLC doesn't accumulate old timeline
            sb.AppendLine($"#EXT-X-DISCONTINUITY-SEQUENCE:{adjustedMediaSequence}");

            // Output segments with their absolute sequence numbers (skip oldest)
            for (int i = skipOldest; i < _segmentCount; i++)
            {
                int idx = (_oldestSegmentIndex + i) % _maxSegments;
                var seg = _segmentBuffers[idx];
                if (seg.IsValid)
                {
                    sb.AppendLine($"#EXTINF:{seg.Duration:F3},");
                    // Use absolute sequence number in URL for stable identification
                    sb.AppendLine($"legacy-segment-{_sessionId}-{seg.SequenceNumber}.ts");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get a specific segment by sequence number.
    /// </summary>
    public byte[]? GetSegment(int sequenceNumber)
    {
        lock (_segmentLock)
        {
            for (int i = 0; i < _maxSegments; i++)
            {
                var seg = _segmentBuffers[i];
                if (seg.IsValid && seg.SequenceNumber == sequenceNumber)
                {
                    return seg.GetData();
                }
            }

        }
        return null;
    }

    /// <summary>
    /// Get a specific segment by absolute sequence number for legacy players.
    /// PTS is adjusted so the first segment in the playlist window starts at 0.
    /// </summary>
    public byte[]? GetLegacySegment(int sequenceNumber)
    {
        lock (_segmentLock)
        {
            if (_segmentCount == 0)
            {
                return null;
            }

            // We skip 2 segments at the start of the legacy playlist to avoid race conditions.
            // The first segment VLC sees is at index 2 (skipOldest=2).
            // We need to adjust PTS so that segment's PTS starts near 0.
            int skipOldest = Math.Min(2, _segmentCount - 1);
            int firstPlaylistIdx = (_oldestSegmentIndex + skipOldest) % _maxSegments;
            var firstPlaylistSeg = _segmentBuffers[firstPlaylistIdx];
            long windowBasePts = firstPlaylistSeg.IsValid ? firstPlaylistSeg.BasePts : 0;

            // Find segment by absolute sequence number
            for (int i = 0; i < _segmentCount; i++)
            {
                int idx = (_oldestSegmentIndex + i) % _maxSegments;
                var seg = _segmentBuffers[idx];
                if (seg.IsValid && seg.SequenceNumber == sequenceNumber)
                {
                    // Adjust PTS to be window-relative
                    // The first segment in the playlist gets PTS ~0
                    return seg.GetLegacyData(windowBasePts);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Reset the HLS stream.
    /// </summary>
    public void Reset()
    {
        lock (_segmentLock)
        {
            for (int i = 0; i < _maxSegments; i++)
            {
                _segmentBuffers[i].Clear();
            }
            _currentStream?.Dispose();
            _currentStream = null;
            _currentParts.Clear();
            _currentPartStartPos = -1;
            _waitingForKeyframe = true;
            _hasData = false;
            _currentSegmentIndex = 0;
            _oldestSegmentIndex = 0;
            _mediaSequence = 0;
            _segmentCount = 0;
            _currentPartIndex = 0;
            _currentPartFrameCount = 0;
            _currentSegmentFrameCount = 0;
            _currentPartStartPts = -1;
            _currentSegmentStartPts = -1;
            _lastFramePts = 0;
            _wallClockInitialized = false;
            _legacyPtsOffset90k = 0;
            _muxer.Reset();
        }

        lock (_waiterLock)
        {
            foreach (var tcs in _partWaiters.Values)
            {
                tcs.TrySetResult(false);
            }
            _partWaiters.Clear();
            _latestCompleteMsn = -1;
            _latestCompletePartIndex = -1;
        }
    }

    private void StartNewSegment()
    {
        _currentStream = new MemoryStream(MaxSegmentSize);
        _muxer.ResetPts();
        _muxer.SetFrameRate(_estimatedFps);
        _segmentStartTime = DateTime.UtcNow;
        _hasData = false;
        _segmentNeedsPatPmt = true;
        _segmentNeedsDiscontinuity = false;
        _segmentNeedsSpsPps = true;
        _currentSegmentFrameCount = 0;
        // Set segment start PTS to current last frame PTS (will be updated on first frame write)
        _currentSegmentStartPts = _lastFramePts > 0 ? _lastFramePts : -1;

        // LL-HLS: Clear parts and prepare for new segment
        if (_llHlsEnabled)
        {
            _currentParts.Clear();
            _currentPartIndex = 0;
            _currentPartStartPos = 0;  // Start at beginning of new segment
            _partStartTime = DateTime.UtcNow;
            _currentPartHasKeyframe = false;
            _currentPartFrameCount = 0;
            _currentPartStartPts = _lastFramePts > 0 ? _lastFramePts : -1;
        }
    }

    private void FinalizeCurrentSegment()
    {
        // Finalize any pending part first
        if (_llHlsEnabled && _currentPartStartPos >= 0 && _currentStream != null && _currentStream.Position > _currentPartStartPos)
        {
            FinalizeCurrentPart();
        }

        if (_currentStream == null || _currentStream.Length == 0)
            return;

        // Calculate duration
        // CRITICAL for LL-HLS: Segment duration MUST equal sum of part durations
        // to avoid fragGap errors in hls.js. The fps estimate changes between
        // part and segment finalization, causing mismatches if we recalculate.
        double duration;

        if (_llHlsEnabled && _currentParts.Count > 0)
        {
            // For LL-HLS: sum the actual part durations to ensure consistency
            duration = _currentParts.Sum(p => p.Duration);
        }
        else if (_currentSegmentFrameCount > 0 && _estimatedFps > 0)
        {
            // For standard HLS: use frame count / fps
            duration = _currentSegmentFrameCount / _estimatedFps;
        }
        else if (_currentSegmentStartPts > 0 && _lastFramePts > _currentSegmentStartPts)
        {
            // Fallback: use PTS delta
            duration = (_lastFramePts - _currentSegmentStartPts) / 1000.0;
        }
        else
        {
            duration = _targetSegmentDurationMs / 1000.0;
        }
        // Clamp to reasonable range
        if (duration < 0.1) duration = 0.1;
        if (duration > 5.0) duration = 5.0;

        // Store in current segment buffer (pass segment start time and base PTS for legacy mode)
        var buffer = _segmentBuffers[_currentSegmentIndex];
        // Get the MUXER's actual PTS at segment start (already in 90kHz ticks)
        // This is critical for legacy PTS adjustment - must match what's actually in the MPEG-TS data
        long basePts90k = _muxer.GetSegmentStartPts();
        if (basePts90k < 0) basePts90k = 90000;  // Fallback if no frames written
        buffer.SetData(_currentStream.GetBuffer(), (int)_currentStream.Length, duration, _mediaSequence + _segmentCount, _segmentStartTime, basePts90k);

        // Store parts in the segment buffer
        if (_llHlsEnabled)
        {
            buffer.SetParts(_currentParts.ToArray());
        }

        _currentStream.Dispose();
        _currentStream = null;

        // Update indices
        _currentSegmentIndex = (_currentSegmentIndex + 1) % _maxSegments;

        if (_segmentCount < _maxSegments)
        {
            _segmentCount++;
        }
        else
        {
            // Evict oldest segment - track its duration for legacy PTS offset
            var evictedSeg = _segmentBuffers[_oldestSegmentIndex];
            if (evictedSeg.IsValid)
            {
                _legacyPtsOffset90k += (long)(evictedSeg.Duration * 90000);
            }
            _oldestSegmentIndex = (_oldestSegmentIndex + 1) % _maxSegments;
            _mediaSequence++;
        }
    }

    private void UpdateFrameRateEstimate()
    {
        var now = DateTime.UtcNow;
        if (_lastFrameTime != default)
        {
            var elapsed = (now - _lastFrameTime).TotalSeconds;
            if (elapsed > 0.001 && elapsed < 1.0)
            {
                var instantFps = 1.0 / elapsed;
                // Use faster adaptation (0.5/0.5) to quickly respond to fps changes
                // This is critical because Anycubic printers throttle from ~15fps to ~4fps
                _estimatedFps = _estimatedFps * 0.5 + instantFps * 0.5;
            }
        }
        _lastFrameTime = now;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_segmentLock)
        {
            _currentStream?.Dispose();
            for (int i = 0; i < _maxSegments; i++)
            {
                _segmentBuffers[i].Dispose();
            }
        }

        lock (_waiterLock)
        {
            foreach (var tcs in _partWaiters.Values)
            {
                tcs.TrySetCanceled();
            }
            _partWaiters.Clear();
        }

        GC.SuppressFinalize(this);
    }

    ~HlsStreamingService()
    {
        Dispose();
    }
}

/// <summary>
/// LL-HLS partial segment data.
/// </summary>
public class HlsPartialSegment
{
    /// <summary>
    /// Index of this part within the segment (0-based).
    /// </summary>
    public int PartIndex { get; set; }

    /// <summary>
    /// MPEG-TS data for this partial segment.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Duration of this part in seconds.
    /// </summary>
    public double Duration { get; set; }

    /// <summary>
    /// Whether this part starts with a keyframe (INDEPENDENT=YES).
    /// </summary>
    public bool Independent { get; set; }

    /// <summary>
    /// When this part was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Reusable segment buffer to avoid allocations.
/// Now supports LL-HLS partial segments.
/// </summary>
internal class HlsSegmentBuffer : IDisposable
{
    private byte[] _buffer;
    private int _length;
    private double _duration;
    private int _sequenceNumber;
    private bool _isValid;
    private DateTime _createdAt;
    private DateTime _startedAt;  // When segment started receiving data (for PROGRAM-DATE-TIME)
    private long _basePts;  // PTS at segment start (for legacy PTS adjustment)
    private HlsPartialSegment[] _parts = Array.Empty<HlsPartialSegment>();

    public bool IsValid => _isValid;
    public double Duration => _duration;
    public int SequenceNumber => _sequenceNumber;
    public DateTime CreatedAt => _createdAt;
    public DateTime StartedAt => _startedAt;  // Wall-clock time when segment started
    public long BasePts => _basePts;  // PTS at segment start
    public int PartCount => _parts.Length;

    public HlsSegmentBuffer(int capacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    }

    public void SetData(byte[] source, int length, double duration, int sequenceNumber, DateTime startedAt, long basePts)
    {
        if (length > _buffer.Length)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(length);
        }

        Array.Copy(source, 0, _buffer, 0, length);
        _length = length;
        _duration = duration;
        _sequenceNumber = sequenceNumber;
        _createdAt = DateTime.UtcNow;
        _startedAt = startedAt;
        _basePts = basePts;
        _isValid = true;
    }

    public void SetParts(HlsPartialSegment[] parts)
    {
        _parts = parts;
    }

    public HlsPartialSegment? GetPart(int index)
    {
        if (index >= 0 && index < _parts.Length)
        {
            return _parts[index];
        }
        return null;
    }

    public byte[] GetData()
    {
        if (!_isValid) return Array.Empty<byte>();

        var result = new byte[_length];
        Array.Copy(_buffer, 0, result, 0, _length);
        return result;
    }

    /// <summary>
    /// Get segment data with adjusted PTS for legacy players like VLC.
    /// </summary>
    /// <param name="ptsOffset">The PTS offset to subtract (original PTS - target PTS)</param>
    public byte[] GetLegacyData(long ptsOffset)
    {
        if (!_isValid) return Array.Empty<byte>();

        var result = new byte[_length];
        Array.Copy(_buffer, 0, result, 0, _length);

        if (ptsOffset <= 0) return result;  // No adjustment needed

        // Adjust PTS/DTS/PCR values in the MPEG-TS data
        AdjustMpegTsTimestamps(result, ptsOffset);

        return result;
    }

    /// <summary>
    /// Adjust PTS, DTS, and PCR timestamps in MPEG-TS data by subtracting an offset.
    /// </summary>
    private static void AdjustMpegTsTimestamps(byte[] data, long offset)
    {
        const int TsPacketSize = 188;
        int pos = 0;

        while (pos + TsPacketSize <= data.Length)
        {
            // Verify sync byte
            if (data[pos] != 0x47)
            {
                pos++;
                continue;
            }

            // Parse TS header
            bool hasAdaptation = (data[pos + 3] & 0x20) != 0;
            bool hasPayload = (data[pos + 3] & 0x10) != 0;
            bool payloadStart = (data[pos + 1] & 0x40) != 0;

            int headerLen = 4;

            // Check adaptation field for PCR
            if (hasAdaptation && pos + 5 < data.Length)
            {
                int adaptLen = data[pos + 4];
                if (adaptLen > 0 && pos + 5 < data.Length)
                {
                    byte adaptFlags = data[pos + 5];
                    bool hasPcr = (adaptFlags & 0x10) != 0;

                    if (hasPcr && adaptLen >= 7 && pos + 11 < data.Length)
                    {
                        // PCR is at pos + 6, 6 bytes
                        AdjustPcr(data, pos + 6, offset);
                    }
                }
                headerLen += 1 + adaptLen;
            }

            // Check for PES header with PTS/DTS
            if (hasPayload && payloadStart && pos + headerLen + 9 < data.Length)
            {
                int pesStart = pos + headerLen;
                // Check for PES start code: 00 00 01
                if (data[pesStart] == 0x00 && data[pesStart + 1] == 0x00 && data[pesStart + 2] == 0x01)
                {
                    byte streamId = data[pesStart + 3];
                    // Video stream IDs: 0xE0-0xEF
                    if (streamId >= 0xE0 && streamId <= 0xEF)
                    {
                        byte ptsFlags = (byte)((data[pesStart + 7] >> 6) & 0x03);
                        int pesHeaderDataLen = data[pesStart + 8];

                        if (ptsFlags >= 2 && pesStart + 14 <= data.Length)  // Has PTS
                        {
                            AdjustPts(data, pesStart + 9, offset);
                        }
                        if (ptsFlags == 3 && pesStart + 19 <= data.Length)  // Has DTS too
                        {
                            AdjustPts(data, pesStart + 14, offset);
                        }
                    }
                }
            }

            pos += TsPacketSize;
        }
    }

    /// <summary>
    /// Adjust a 5-byte PTS/DTS field by subtracting an offset.
    /// </summary>
    private static void AdjustPts(byte[] data, int offset, long ptsOffset)
    {
        // Read PTS (33 bits spread across 5 bytes)
        long pts = ((long)(data[offset] >> 1) & 0x07) << 30;
        pts |= (long)data[offset + 1] << 22;
        pts |= (long)(data[offset + 2] >> 1) << 15;
        pts |= (long)data[offset + 3] << 7;
        pts |= (long)(data[offset + 4] >> 1);

        // Subtract offset
        pts -= ptsOffset;
        if (pts < 0) pts = 0;

        // Write back (preserve marker bits)
        byte marker = (byte)(data[offset] & 0xF1);
        data[offset] = (byte)(marker | ((pts >> 29) & 0x0E));
        data[offset + 1] = (byte)((pts >> 22) & 0xFF);
        data[offset + 2] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        data[offset + 3] = (byte)((pts >> 7) & 0xFF);
        data[offset + 4] = (byte)(0x01 | ((pts << 1) & 0xFE));
    }

    /// <summary>
    /// Adjust a 6-byte PCR field by subtracting an offset.
    /// </summary>
    private static void AdjustPcr(byte[] data, int offset, long ptsOffset)
    {
        // Read PCR base (33 bits) - ignore extension for simplicity
        long pcr = (long)data[offset] << 25;
        pcr |= (long)data[offset + 1] << 17;
        pcr |= (long)data[offset + 2] << 9;
        pcr |= (long)data[offset + 3] << 1;
        pcr |= (long)(data[offset + 4] >> 7) & 0x01;

        // Subtract offset
        pcr -= ptsOffset;
        if (pcr < 0) pcr = 0;

        // Write back (preserve reserved bits and extension)
        data[offset] = (byte)((pcr >> 25) & 0xFF);
        data[offset + 1] = (byte)((pcr >> 17) & 0xFF);
        data[offset + 2] = (byte)((pcr >> 9) & 0xFF);
        data[offset + 3] = (byte)((pcr >> 1) & 0xFF);
        data[offset + 4] = (byte)((data[offset + 4] & 0x7F) | (int)((pcr & 0x01) << 7));
    }

    public void Clear()
    {
        _isValid = false;
        _length = 0;
        _createdAt = default;
        _startedAt = default;
        _basePts = 0;
        _parts = Array.Empty<HlsPartialSegment>();
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
        }
    }
}
