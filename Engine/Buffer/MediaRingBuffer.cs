using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;

namespace ClipThat.Engine.Buffer;

/// <summary>
/// Identifies the logical media stream a compressed packet belongs to inside the replay buffer.
/// </summary>
public enum MediaTrackType
{
    Video = 0,
    SystemAudio = 1,
    MicrophoneAudio = 2
}

/// <summary>
/// Holds a compressed video or audio packet backed by a pooled byte array from <see cref="ArrayPool{T}.Shared"/>.
/// Supports deterministic, thread-safe single-return cleanup and zero-copy retention for replay snapshots.
/// </summary>
public sealed class EncodedPacket : IDisposable
{
    private readonly PooledBuffer _backing;
    private int _disposed;

    public EncodedPacket(
        MediaTrackType trackType,
        byte[] buffer,
        int length,
        long timestamp100Ns,
        long duration100Ns,
        bool isKeyframe)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (length < 0 || length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Długość pakietu wykracza poza rozmiar bufora.");
        }

        TrackType = trackType;
        Length = length;
        Timestamp100Ns = timestamp100Ns;
        Duration100Ns = duration100Ns;
        IsKeyframe = isKeyframe;
        _backing = new PooledBuffer(buffer);
    }

    private EncodedPacket(
        MediaTrackType trackType,
        PooledBuffer backing,
        int length,
        long timestamp100Ns,
        long duration100Ns,
        bool isKeyframe)
    {
        TrackType = trackType;
        _backing = backing;
        Length = length;
        Timestamp100Ns = timestamp100Ns;
        Duration100Ns = duration100Ns;
        IsKeyframe = isKeyframe;
    }

    public MediaTrackType TrackType { get; }

    public byte[] Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _backing.Array;
        }
    }

    public int Length { get; }

    public long Timestamp100Ns { get; }

    public long Duration100Ns { get; }

    public bool IsKeyframe { get; }

    public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Length);

    /// <summary>
    /// Creates an independently disposable packet handle sharing the underlying rented buffer via atomic ref-counting.
    /// </summary>
    internal EncodedPacket Retain()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _backing.AddRef();
        return new EncodedPacket(TrackType, _backing, Length, Timestamp100Ns, Duration100Ns, IsKeyframe);
    }

    /// <summary>
    /// Releases this handle and returns the underlying rented buffer to <see cref="ArrayPool{T}.Shared"/>
    /// strictly once when the last owner disposes it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backing.Release();
    }

    private sealed class PooledBuffer(byte[] array)
    {
        private byte[]? _array = array;
        private int _refCount = 1;

        public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(EncodedPacket));

        public void AddRef()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    throw new ObjectDisposedException(nameof(EncodedPacket));
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return;
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                byte[]? toReturn = Interlocked.Exchange(ref _array, null);
                if (toReturn is not null)
                {
                    ArrayPool<byte>.Shared.Return(toReturn);
                }
            }
        }
    }
}

/// <summary>
/// Disposable read-only snapshot of retained packets from <see cref="MediaRingBuffer"/>.
/// Disposing the snapshot releases all retained packet handles deterministically.
/// </summary>
public sealed class EncodedPacketSnapshot : IReadOnlyList<EncodedPacket>, IDisposable
{
    private readonly List<EncodedPacket> _packets;
    private int _disposed;

    internal EncodedPacketSnapshot(List<EncodedPacket> packets, byte[]? videoSequenceHeader)
    {
        _packets = packets;
        VideoSequenceHeader = videoSequenceHeader;

        long totalBytes = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            totalBytes += packets[i].Length;
        }

        TotalBytes = totalBytes;
    }

    public byte[]? VideoSequenceHeader { get; }

    public long TotalBytes { get; }

    public TimeSpan Duration
    {
        get
        {
            if (_packets.Count < 2)
            {
                return TimeSpan.Zero;
            }

            long spanTicks = _packets[^1].Timestamp100Ns - _packets[0].Timestamp100Ns;
            return spanTicks > 0 ? TimeSpan.FromTicks(spanTicks) : TimeSpan.Zero;
        }
    }

    public int Count => _packets.Count;

    public EncodedPacket this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _packets[index];
        }
    }

    public IEnumerator<EncodedPacket> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _packets.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (int i = 0; i < _packets.Count; i++)
        {
            _packets[i].Dispose();
        }

        _packets.Clear();
    }
}

/// <summary>
/// Thread-safe in-memory circular buffer storing compressed video and audio packets
/// with GOP/keyframe-aligned eviction and a 1024 MB free-RAM system stability safety guard.
/// </summary>
public sealed class MediaRingBuffer : IDisposable
{
    public const ulong MinimumFreePhysicalMemoryBytes = 1024UL * 1024UL * 1024UL;
    private const long MemoryCheckIntervalMs = 250;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly LinkedList<EncodedPacket> _packets = new();

    private long _maxDuration100Ns;
    private long _bufferedBytes;
    private long _lastMemoryCheckTickMs;
    private byte[]? _videoSequenceHeader;
    private int _disposed;

    public MediaRingBuffer(int bufferDurationMinutes = 5)
    {
        int clamped = Math.Clamp(bufferDurationMinutes, 1, 10);
        _maxDuration100Ns = TimeSpan.FromMinutes(clamped).Ticks;
    }

    public int BufferDurationMinutes
    {
        get
        {
            long ticks = Volatile.Read(ref _maxDuration100Ns);
            return (int)Math.Max(1, ticks / TimeSpan.TicksPerMinute);
        }
    }

    public long BufferedBytes => Volatile.Read(ref _bufferedBytes);

    public int PacketCount
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return 0;
            }

            _lock.EnterReadLock();
            try
            {
                return _packets.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return TimeSpan.Zero;
            }

            _lock.EnterReadLock();
            try
            {
                if (_packets.First is null || _packets.Last is null)
                {
                    return TimeSpan.Zero;
                }

                long span = _packets.Last.Value.Timestamp100Ns - _packets.First.Value.Timestamp100Ns;
                return span > 0 ? TimeSpan.FromTicks(span) : TimeSpan.Zero;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Stores the latest H.264 Annex-B SPS/PPS sequence header bytes associated with the buffered stream.
    /// </summary>
    public void SetVideoSequenceHeader(ReadOnlySpan<byte> headerBytes)
    {
        if (Volatile.Read(ref _disposed) != 0 || headerBytes.IsEmpty)
        {
            return;
        }

        byte[] copy = headerBytes.ToArray();
        _lock.EnterWriteLock();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _videoSequenceHeader = copy;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Updates the maximum ring buffer retention window and immediately trims excess GOP segments.
    /// </summary>
    public void SetBufferDuration(int bufferDurationMinutes)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        int clamped = Math.Clamp(bufferDurationMinutes, 1, 10);
        long newMaxTicks = TimeSpan.FromMinutes(clamped).Ticks;

        _lock.EnterWriteLock();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _maxDuration100Ns, newMaxTicks);
            EvictExpiredPacketsLocked();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Appends a compressed packet and takes ownership of its lifetime.
    /// Automatically enforces duration limits, keyframe alignment, and the 1024 MB free physical RAM guard.
    /// </summary>
    public void AddPacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (Volatile.Read(ref _disposed) != 0)
        {
            packet.Dispose();
            return;
        }

        bool checkMemoryPressure = packet.TrackType == MediaTrackType.Video && packet.IsKeyframe;
        long nowTickMs = Environment.TickCount64;
        if (!checkMemoryPressure && nowTickMs - Volatile.Read(ref _lastMemoryCheckTickMs) >= MemoryCheckIntervalMs)
        {
            checkMemoryPressure = true;
        }

        bool isMemoryLow = false;
        if (checkMemoryPressure)
        {
            Volatile.Write(ref _lastMemoryCheckTickMs, nowTickMs);
            isMemoryLow = IsPhysicalMemoryBelowThreshold();
        }

        _lock.EnterWriteLock();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                packet.Dispose();
                return;
            }

            _packets.AddLast(packet);
            _bufferedBytes += packet.Length;

            EnsureLeadingVideoKeyframeLocked();
            EvictExpiredPacketsLocked();

            if (isMemoryLow)
            {
                TrimOldestGopsForMemoryPressureLocked();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Extension point for Step 2B audio encoders to enqueue encoded system or microphone audio packets.
    /// </summary>
    public void AddAudioPacket(
        MediaTrackType trackType,
        byte[] rentedBuffer,
        int length,
        long timestamp100Ns,
        long duration100Ns)
    {
        if (trackType is not (MediaTrackType.SystemAudio or MediaTrackType.MicrophoneAudio))
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
            throw new ArgumentException("Oczekiwano ścieżki audio (SystemAudio lub MicrophoneAudio).", nameof(trackType));
        }

        var packet = new EncodedPacket(
            trackType,
            rentedBuffer,
            length,
            timestamp100Ns,
            duration100Ns,
            isKeyframe: true);

        AddPacket(packet);
    }

    /// <summary>
    /// Safely retains all buffered packets starting from the oldest valid video keyframe up to the latest packet
    /// without copying packet payloads or blocking live capture beyond a brief read lock.
    /// </summary>
    public EncodedPacketSnapshot SnapshotPackets()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new EncodedPacketSnapshot(new List<EncodedPacket>(), null);
        }

        _lock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _packets.Count == 0)
            {
                return new EncodedPacketSnapshot(new List<EncodedPacket>(), _videoSequenceHeader?.ToArray());
            }

            LinkedListNode<EncodedPacket>? firstKeyframeNode = FindFirstVideoKeyframeLocked();
            if (firstKeyframeNode is null)
            {
                return new EncodedPacketSnapshot(new List<EncodedPacket>(), _videoSequenceHeader?.ToArray());
            }

            long keyframeTimestamp = firstKeyframeNode.Value.Timestamp100Ns;
            var retained = new List<EncodedPacket>(_packets.Count);

            try
            {
                // Include any audio packets slightly preceding the first video keyframe node
                // whose presentation timestamp falls within the replay window.
                LinkedListNode<EncodedPacket>? scan = _packets.First;
                while (scan is not null && scan != firstKeyframeNode)
                {
                    if (scan.Value.TrackType != MediaTrackType.Video &&
                        scan.Value.Timestamp100Ns >= keyframeTimestamp)
                    {
                        retained.Add(scan.Value.Retain());
                    }

                    scan = scan.Next;
                }

                LinkedListNode<EncodedPacket>? current = firstKeyframeNode;
                while (current is not null)
                {
                    retained.Add(current.Value.Retain());
                    current = current.Next;
                }

                return new EncodedPacketSnapshot(retained, _videoSequenceHeader?.ToArray());
            }
            catch
            {
                for (int i = 0; i < retained.Count; i++)
                {
                    retained[i].Dispose();
                }

                throw;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Disposes and removes all currently buffered packets, returning all buffers to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public void Clear()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            ClearLocked();
            _videoSequenceHeader = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            ClearLocked();
            _videoSequenceHeader = null;
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }

    private void ClearLocked()
    {
        while (_packets.First is not null)
        {
            RemoveFirstLocked();
        }

        Volatile.Write(ref _bufferedBytes, 0);
    }

    private void RemoveFirstLocked()
    {
        LinkedListNode<EncodedPacket>? first = _packets.First;
        if (first is null)
        {
            return;
        }

        EncodedPacket packet = first.Value;
        _packets.RemoveFirst();
        _bufferedBytes = Math.Max(0, _bufferedBytes - packet.Length);
        packet.Dispose();
    }

    /// <summary>
    /// Drops any leading non-keyframe video packets so the oldest video packet in the buffer
    /// is always a decodable IDR keyframe.
    /// </summary>
    private void EnsureLeadingVideoKeyframeLocked()
    {
        LinkedListNode<EncodedPacket>? node = _packets.First;
        while (node is not null)
        {
            EncodedPacket packet = node.Value;
            if (packet.TrackType == MediaTrackType.Video)
            {
                if (packet.IsKeyframe)
                {
                    return;
                }

                LinkedListNode<EncodedPacket>? next = node.Next;
                _packets.Remove(node);
                _bufferedBytes = Math.Max(0, _bufferedBytes - packet.Length);
                packet.Dispose();
                node = next;
                continue;
            }

            node = node.Next;
        }
    }

    private void EvictExpiredPacketsLocked()
    {
        if (_packets.First is null || _packets.Last is null)
        {
            return;
        }

        long maxDuration100Ns = Volatile.Read(ref _maxDuration100Ns);
        long latestTimestamp = _packets.Last.Value.Timestamp100Ns;

        // If no video packets are present yet (audio-only startup window), trim old audio packets directly.
        LinkedListNode<EncodedPacket>? firstVideoKeyframe = FindFirstVideoKeyframeLocked();
        if (firstVideoKeyframe is null)
        {
            while (_packets.First is not null &&
                   latestTimestamp - _packets.First.Value.Timestamp100Ns > maxDuration100Ns)
            {
                RemoveFirstLocked();
            }

            return;
        }

        // Trim any stray audio packets significantly older than the first video keyframe.
        while (_packets.First is not null &&
               _packets.First != firstVideoKeyframe &&
               _packets.First.Value.Timestamp100Ns < firstVideoKeyframe.Value.Timestamp100Ns)
        {
            RemoveFirstLocked();
        }

        // Evict whole oldest GOP segments whenever buffered timespan exceeds BufferDurationMinutes,
        // preserving the invariant that the oldest remaining video packet is always a keyframe.
        while (_packets.First is not null && _packets.Last is not null)
        {
            firstVideoKeyframe = FindFirstVideoKeyframeLocked();
            if (firstVideoKeyframe is null)
            {
                break;
            }

            long oldestTimestamp = firstVideoKeyframe.Value.Timestamp100Ns;
            if (latestTimestamp - oldestTimestamp <= maxDuration100Ns)
            {
                break;
            }

            LinkedListNode<EncodedPacket>? nextVideoKeyframe = FindNextVideoKeyframeLocked(firstVideoKeyframe);
            if (nextVideoKeyframe is null)
            {
                break;
            }

            RemoveUntilNodeLocked(nextVideoKeyframe);
        }
    }

    private void TrimOldestGopsForMemoryPressureLocked()
    {
        // Drop up to 4 oldest GOP segments per low-memory trigger (preserving at least the newest GOP)
        // to aggressively relieve RAM usage without discarding the active replay tail.
        const int maxGopsToTrimPerPass = 4;

        for (int trimmed = 0; trimmed < maxGopsToTrimPerPass; trimmed++)
        {
            LinkedListNode<EncodedPacket>? firstKeyframe = FindFirstVideoKeyframeLocked();
            if (firstKeyframe is null)
            {
                break;
            }

            LinkedListNode<EncodedPacket>? nextKeyframe = FindNextVideoKeyframeLocked(firstKeyframe);
            if (nextKeyframe is null)
            {
                break;
            }

            RemoveUntilNodeLocked(nextKeyframe);

            if (!IsPhysicalMemoryBelowThreshold())
            {
                break;
            }
        }
    }

    private void RemoveUntilNodeLocked(LinkedListNode<EncodedPacket> targetNode)
    {
        while (_packets.First is not null && _packets.First != targetNode)
        {
            RemoveFirstLocked();
        }
    }

    private LinkedListNode<EncodedPacket>? FindFirstVideoKeyframeLocked()
    {
        for (LinkedListNode<EncodedPacket>? node = _packets.First; node is not null; node = node.Next)
        {
            if (node.Value.TrackType == MediaTrackType.Video && node.Value.IsKeyframe)
            {
                return node;
            }
        }

        return null;
    }

    private static LinkedListNode<EncodedPacket>? FindNextVideoKeyframeLocked(LinkedListNode<EncodedPacket> currentKeyframe)
    {
        for (LinkedListNode<EncodedPacket>? node = currentKeyframe.Next; node is not null; node = node.Next)
        {
            if (node.Value.TrackType == MediaTrackType.Video && node.Value.IsKeyframe)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Queries available physical system RAM via Win32 <c>GlobalMemoryStatusEx</c>.
    /// </summary>
    public static ulong GetAvailablePhysicalMemoryBytes()
    {
        var memoryStatus = new MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        return GlobalMemoryStatusEx(ref memoryStatus)
            ? memoryStatus.ullAvailPhys
            : ulong.MaxValue;
    }

    private static bool IsPhysicalMemoryBelowThreshold()
        => GetAvailablePhysicalMemoryBytes() < MinimumFreePhysicalMemoryBytes;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
