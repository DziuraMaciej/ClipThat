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
    CombinedMixAudio = 1,
    SystemAudio = 2,
    MicrophoneAudio = 3
}

/// <summary>
/// Holds a compressed video or audio packet backed by exact-size unmanaged memory (or a legacy pooled byte array).
/// Supports deterministic, thread-safe single-free cleanup and zero-copy retention for replay snapshots
/// without inflating or fragmenting the .NET Large Object Heap (LOH).
/// </summary>
public sealed unsafe class EncodedPacket : IDisposable
{
    private readonly PacketBacking _backing;
    private int _disposed;

    public EncodedPacket(
        MediaTrackType trackType,
        byte[] buffer,
        int length,
        long timestamp100Ns,
        long duration100Ns,
        bool isKeyframe)
        : this(trackType, buffer, length, timestamp100Ns, duration100Ns, isKeyframe, returnToArrayPool: true)
    {
    }

    internal EncodedPacket(
        MediaTrackType trackType,
        byte[] buffer,
        int length,
        long timestamp100Ns,
        long duration100Ns,
        bool isKeyframe,
        bool returnToArrayPool)
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
        _backing = new PacketBacking(buffer, returnToArrayPool);
    }

    private EncodedPacket(
        MediaTrackType trackType,
        PacketBacking backing,
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

    /// <summary>
    /// Copies <paramref name="length"/> bytes directly from an unmanaged Media Foundation buffer pointer
    /// into an exact-size unmanaged allocation freed deterministically on <see cref="Dispose"/>.
    /// Avoids power-of-2 <see cref="ArrayPool{T}.Shared"/> over-allocation and LOH retention.
    /// </summary>
    public static EncodedPacket CreateFromNativeMemory(
        MediaTrackType trackType,
        nint sourcePtr,
        int length,
        long timestamp100Ns,
        long duration100Ns,
        bool isKeyframe)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Długość pakietu nie może być ujemna.");
        }

        if (length > 0 && sourcePtr == nint.Zero)
        {
            throw new ArgumentException("Wskaźnik źródłowy bufora natywnego jest pusty.", nameof(sourcePtr));
        }

        PacketBacking backing = PacketBacking.AllocateNativeCopy(sourcePtr, length);
        return new EncodedPacket(trackType, backing, length, timestamp100Ns, duration100Ns, isKeyframe);
    }

    public MediaTrackType TrackType { get; }

    public byte[] Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _backing.GetOrMaterializeManagedBuffer(Length);
        }
    }

    public int Length { get; }

    public long Timestamp100Ns { get; }

    public long Duration100Ns { get; }

    public bool IsKeyframe { get; }

    public ReadOnlySpan<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _backing.GetSpan(Length);
        }
    }

    /// <summary>
    /// Creates an independently disposable packet handle sharing the underlying payload buffer via atomic ref-counting.
    /// </summary>
    internal EncodedPacket Retain()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _backing.AddRef();
        return new EncodedPacket(TrackType, _backing, Length, Timestamp100Ns, Duration100Ns, IsKeyframe);
    }

    /// <summary>
    /// Releases this handle and frees the underlying native buffer (or returns a legacy rented array)
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

    private sealed class PacketBacking
    {
        private nint _nativePtr;
        private byte[]? _managedArray;
        private readonly bool _returnToArrayPool;
        private readonly bool _isEmpty;
        private int _refCount = 1;

        public PacketBacking(byte[] array, bool returnToArrayPool)
        {
            _managedArray = array;
            _returnToArrayPool = returnToArrayPool;
            GC.SuppressFinalize(this);
        }

        private PacketBacking(nint nativePtr, bool isEmpty)
        {
            _nativePtr = nativePtr;
            _isEmpty = isEmpty;
            if (isEmpty)
            {
                GC.SuppressFinalize(this);
            }
        }

        public static PacketBacking AllocateNativeCopy(nint sourcePtr, int length)
        {
            if (length == 0)
            {
                return new PacketBacking(nint.Zero, isEmpty: true);
            }

            void* dest = NativeMemory.Alloc((nuint)length);
            System.Buffer.MemoryCopy((void*)sourcePtr, dest, (nuint)length, (nuint)length);
            return new PacketBacking((nint)dest, isEmpty: false);
        }

        public ReadOnlySpan<byte> GetSpan(int length)
        {
            if (length == 0 && _isEmpty)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            nint nativePtr = Volatile.Read(ref _nativePtr);
            if (nativePtr != nint.Zero)
            {
                return new ReadOnlySpan<byte>((void*)nativePtr, length);
            }

            byte[]? managed = Volatile.Read(ref _managedArray);
            if (managed is not null)
            {
                return managed.AsSpan(0, length);
            }

            throw new ObjectDisposedException(nameof(EncodedPacket));
        }

        public byte[] GetOrMaterializeManagedBuffer(int length)
        {
            byte[]? managed = Volatile.Read(ref _managedArray);
            if (managed is not null)
            {
                return managed;
            }

            if (length == 0 && _isEmpty)
            {
                return System.Array.Empty<byte>();
            }

            nint nativePtr = Volatile.Read(ref _nativePtr);
            if (nativePtr == nint.Zero)
            {
                throw new ObjectDisposedException(nameof(EncodedPacket));
            }

            byte[] copy = GC.AllocateUninitializedArray<byte>(length);
            new ReadOnlySpan<byte>((void*)nativePtr, length).CopyTo(copy);
            return copy;
        }

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
                FreeResources();
                GC.SuppressFinalize(this);
            }
        }

        private void FreeResources()
        {
            nint ptr = Interlocked.Exchange(ref _nativePtr, nint.Zero);
            if (ptr != nint.Zero)
            {
                NativeMemory.Free((void*)ptr);
            }

            byte[]? toReturn = Interlocked.Exchange(ref _managedArray, null);
            if (toReturn is not null && _returnToArrayPool)
            {
                ArrayPool<byte>.Shared.Return(toReturn);
            }
        }

        ~PacketBacking()
        {
            FreeResources();
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

    internal EncodedPacketSnapshot(
        List<EncodedPacket> packets,
        byte[]? videoSequenceHeader,
        byte[]? systemAudioUserData = null,
        byte[]? microphoneAudioUserData = null,
        byte[]? combinedMixAudioUserData = null)
    {
        _packets = packets;
        VideoSequenceHeader = videoSequenceHeader;
        SystemAudioUserData = systemAudioUserData;
        MicrophoneAudioUserData = microphoneAudioUserData;
        CombinedMixAudioUserData = combinedMixAudioUserData;

        long totalBytes = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            totalBytes += packets[i].Length;
        }

        TotalBytes = totalBytes;
    }

    public byte[]? VideoSequenceHeader { get; }

    public byte[]? SystemAudioUserData { get; }

    public byte[]? MicrophoneAudioUserData { get; }

    public byte[]? CombinedMixAudioUserData { get; }

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

    private readonly object _syncLock = new();
    private readonly LinkedList<EncodedPacket> _packets = new();
    private readonly LinkedList<LinkedListNode<EncodedPacket>> _videoKeyframes = new();

    private long _maxDuration100Ns;
    private long _bufferedBytes;
    private long _latestTimestamp100Ns = long.MinValue;
    private long _lastMemoryCheckTickMs;
    private byte[]? _videoSequenceHeader;
    private byte[]? _combinedMixAudioUserData;
    private byte[]? _systemAudioUserData;
    private byte[]? _microphoneAudioUserData;
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

            lock (_syncLock)
            {
                return Volatile.Read(ref _disposed) != 0 ? 0 : _packets.Count;
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

            lock (_syncLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || _packets.First is null || _packets.Last is null)
                {
                    return TimeSpan.Zero;
                }

                long span = _latestTimestamp100Ns - _packets.First.Value.Timestamp100Ns;
                return span > 0 ? TimeSpan.FromTicks(span) : TimeSpan.Zero;
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
        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _videoSequenceHeader = copy;
        }
    }

    /// <summary>
    /// Stores the latest AAC <c>MF_MT_USER_DATA</c> (<c>HEAACWAVEINFO</c> + <c>AudioSpecificConfig</c>)
    /// codec configuration blob for the given audio track so the MP4 muxer can write an exact <c>esds</c> box.
    /// </summary>
    public void SetAudioUserData(MediaTrackType trackType, ReadOnlySpan<byte> userData)
    {
        if (Volatile.Read(ref _disposed) != 0 || userData.IsEmpty)
        {
            return;
        }

        if (trackType is not (MediaTrackType.CombinedMixAudio or MediaTrackType.SystemAudio or MediaTrackType.MicrophoneAudio))
        {
            return;
        }

        byte[] copy = userData.ToArray();
        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            switch (trackType)
            {
                case MediaTrackType.CombinedMixAudio:
                    _combinedMixAudioUserData = copy;
                    break;
                case MediaTrackType.SystemAudio:
                    _systemAudioUserData = copy;
                    break;
                case MediaTrackType.MicrophoneAudio:
                    _microphoneAudioUserData = copy;
                    break;
            }
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

        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _maxDuration100Ns, newMaxTicks);
            EvictExpiredPacketsLocked();
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

        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                packet.Dispose();
                return;
            }

            // Drop non-keyframe leading video packets immediately in O(1) when no keyframe exists yet.
            if (packet.TrackType == MediaTrackType.Video && !packet.IsKeyframe && _videoKeyframes.Count == 0)
            {
                packet.Dispose();
                return;
            }

            LinkedListNode<EncodedPacket> node = _packets.AddLast(packet);
            if (packet.TrackType == MediaTrackType.Video && packet.IsKeyframe)
            {
                _videoKeyframes.AddLast(node);
            }

            Volatile.Write(ref _bufferedBytes, _bufferedBytes + packet.Length);
            if (packet.Timestamp100Ns > _latestTimestamp100Ns)
            {
                _latestTimestamp100Ns = packet.Timestamp100Ns;
            }

            EvictExpiredPacketsLocked();

            if (isMemoryLow)
            {
                TrimOldestGopsForMemoryPressureLocked();
            }
        }
    }

    /// <summary>
    /// Enqueues encoded combined-mix, system, or microphone audio packets into the ring buffer.
    /// </summary>
    public void AddAudioPacket(
        MediaTrackType trackType,
        byte[] rentedBuffer,
        int length,
        long timestamp100Ns,
        long duration100Ns)
    {
        if (trackType is not (MediaTrackType.CombinedMixAudio or MediaTrackType.SystemAudio or MediaTrackType.MicrophoneAudio))
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
            throw new ArgumentException("Oczekiwano ścieżki audio (CombinedMixAudio, SystemAudio lub MicrophoneAudio).", nameof(trackType));
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

        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _packets.Count == 0)
            {
                return new EncodedPacketSnapshot(
                    new List<EncodedPacket>(),
                    _videoSequenceHeader?.ToArray(),
                    _systemAudioUserData?.ToArray(),
                    _microphoneAudioUserData?.ToArray(),
                    _combinedMixAudioUserData?.ToArray());
            }

            LinkedListNode<EncodedPacket>? firstKeyframeNode = FindFirstVideoKeyframeLocked();
            if (firstKeyframeNode is null)
            {
                return new EncodedPacketSnapshot(
                    new List<EncodedPacket>(),
                    _videoSequenceHeader?.ToArray(),
                    _systemAudioUserData?.ToArray(),
                    _microphoneAudioUserData?.ToArray(),
                    _combinedMixAudioUserData?.ToArray());
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

                return new EncodedPacketSnapshot(
                    retained,
                    _videoSequenceHeader?.ToArray(),
                    _systemAudioUserData?.ToArray(),
                    _microphoneAudioUserData?.ToArray(),
                    _combinedMixAudioUserData?.ToArray());
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
    }

    /// <summary>
    /// Disposes and removes all currently buffered packets, freeing native buffers and returning pooled arrays.
    /// </summary>
    public void Clear()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_syncLock)
        {
            ClearLocked();
            _videoSequenceHeader = null;
            _combinedMixAudioUserData = null;
            _systemAudioUserData = null;
            _microphoneAudioUserData = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_syncLock)
        {
            ClearLocked();
            _videoSequenceHeader = null;
            _combinedMixAudioUserData = null;
            _systemAudioUserData = null;
            _microphoneAudioUserData = null;
        }
    }

    private void ClearLocked()
    {
        while (_packets.First is not null)
        {
            RemoveFirstLocked();
        }

        _videoKeyframes.Clear();
        _latestTimestamp100Ns = long.MinValue;
        Volatile.Write(ref _bufferedBytes, 0);
    }

    private void RemoveFirstLocked()
    {
        LinkedListNode<EncodedPacket>? first = _packets.First;
        if (first is null)
        {
            return;
        }

        RemoveNodeLocked(first);
    }

    private void RemoveNodeLocked(LinkedListNode<EncodedPacket> node)
    {
        if (_videoKeyframes.First is not null && ReferenceEquals(_videoKeyframes.First.Value, node))
        {
            _videoKeyframes.RemoveFirst();
        }

        EncodedPacket packet = node.Value;
        _packets.Remove(node);
        Volatile.Write(ref _bufferedBytes, Math.Max(0, _bufferedBytes - packet.Length));
        packet.Dispose();
    }

    private void EvictExpiredPacketsLocked()
    {
        if (_packets.First is null || _packets.Last is null)
        {
            return;
        }

        long maxDuration100Ns = Volatile.Read(ref _maxDuration100Ns);
        long latestTimestamp = _latestTimestamp100Ns != long.MinValue
            ? _latestTimestamp100Ns
            : _packets.Last.Value.Timestamp100Ns;

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

        // Trim any stray audio packets older than the first video keyframe.
        while (_packets.First is not null &&
               _packets.First != firstVideoKeyframe &&
               _packets.First.Value.Timestamp100Ns < firstVideoKeyframe.Value.Timestamp100Ns)
        {
            RemoveFirstLocked();
        }

        // Evict whole oldest GOP segments in O(1) per GOP whenever buffered timespan exceeds BufferDurationMinutes,
        // preserving the invariant that the oldest remaining video packet is always a keyframe.
        while (_videoKeyframes.Count >= 2)
        {
            firstVideoKeyframe = _videoKeyframes.First!.Value;
            long oldestTimestamp = firstVideoKeyframe.Value.Timestamp100Ns;
            if (latestTimestamp - oldestTimestamp <= maxDuration100Ns)
            {
                break;
            }

            LinkedListNode<EncodedPacket> nextVideoKeyframe = _videoKeyframes.First.Next!.Value;
            RemoveUntilNodeLocked(nextVideoKeyframe);
        }

        // Static-desktop safeguard: when only a single video keyframe exists and audio keeps arriving
        // beyond maxDuration100Ns, evict expired audio packets after that keyframe so RAM stays bounded.
        if (_videoKeyframes.Count == 1)
        {
            firstVideoKeyframe = _videoKeyframes.First!.Value;
            long audioCutoff = latestTimestamp - maxDuration100Ns;
            if (audioCutoff > firstVideoKeyframe.Value.Timestamp100Ns)
            {
                TrimExpiredAudioAfterNodeLocked(firstVideoKeyframe, audioCutoff, maxPacketsToRemove: int.MaxValue);
            }
        }
    }

    private void TrimOldestGopsForMemoryPressureLocked()
    {
        // Drop up to 4 oldest GOP segments per low-memory trigger (preserving at least the newest GOP)
        // to aggressively relieve RAM usage without discarding the active replay tail.
        const int maxGopsToTrimPerPass = 4;

        for (int trimmed = 0; trimmed < maxGopsToTrimPerPass; trimmed++)
        {
            if (_videoKeyframes.Count < 2)
            {
                break;
            }

            LinkedListNode<EncodedPacket> nextKeyframe = _videoKeyframes.First!.Next!.Value;
            RemoveUntilNodeLocked(nextKeyframe);

            if (!IsPhysicalMemoryBelowThreshold())
            {
                return;
            }
        }

        // If only one (or zero) video keyframes remain under memory pressure (e.g. long static screen),
        // trim oldest audio packets so the memory guard can still free RAM.
        if (_videoKeyframes.Count == 1)
        {
            TrimExpiredAudioAfterNodeLocked(_videoKeyframes.First!.Value, long.MaxValue, maxPacketsToRemove: 256);
        }
        else if (_videoKeyframes.Count == 0)
        {
            for (int i = 0; i < 256 && _packets.Count > 1; i++)
            {
                RemoveFirstLocked();
            }
        }
    }

    private void TrimExpiredAudioAfterNodeLocked(
        LinkedListNode<EncodedPacket> anchorNode,
        long audioCutoff100Ns,
        int maxPacketsToRemove)
    {
        int removed = 0;
        LinkedListNode<EncodedPacket>? node = anchorNode.Next;
        while (node is not null && removed < maxPacketsToRemove)
        {
            LinkedListNode<EncodedPacket>? next = node.Next;
            EncodedPacket packet = node.Value;
            if (packet.TrackType != MediaTrackType.Video)
            {
                if (packet.Timestamp100Ns >= audioCutoff100Ns)
                {
                    break;
                }

                RemoveNodeLocked(node);
                removed++;
            }

            node = next;
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
        => _videoKeyframes.First?.Value;

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
