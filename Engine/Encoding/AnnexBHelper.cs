using System.IO;

namespace ClipThat.Engine.Encoding;

/// <summary>
/// Shared H.264 Annex-B NAL unit parsing helpers (IDR keyframe detection and SPS/PPS extraction)
/// used by <see cref="MfH264Encoder"/>, <see cref="AmfH264Encoder"/>, and <see cref="Muxing.Mp4ReplayMuxer"/>.
/// </summary>
internal static class AnnexBHelper
{
    /// <summary>
    /// Inspects Annex-B start codes (<c>00 00 01</c> or <c>00 00 00 01</c>) and returns <see langword="true"/>
    /// when a true H.264 IDR slice (<c>nalUnitType == 5</c>) is present.
    /// </summary>
    public static bool ContainsKeyframeNal(ReadOnlySpan<byte> data)
    {
        int pos = FindNextStartCode(data, 0, out int startCodeLen);
        while (pos >= 0 && pos + startCodeLen < data.Length)
        {
            int nalStart = pos + startCodeLen;
            int nalUnitType = data[nalStart] & 0x1F;
            if (nalUnitType == 5)
            {
                return true;
            }

            pos = FindNextStartCode(data, nalStart + 1, out startCodeLen);
        }

        return false;
    }

    /// <summary>
    /// Zero-allocation check returning <see langword="true"/> when <paramref name="data"/> contains
    /// both an Annex-B SPS (NAL type 7) and PPS (NAL type 8).
    /// </summary>
    public static bool ContainsSpsPps(ReadOnlySpan<byte> data)
    {
        int pos = FindNextStartCode(data, 0, out int startCodeLen);
        bool hasSps = false;
        bool hasPps = false;

        while (pos >= 0 && pos + startCodeLen < data.Length)
        {
            int nalStart = pos + startCodeLen;
            int nalType = data[nalStart] & 0x1F;
            if (nalType == 7)
            {
                hasSps = true;
            }
            else if (nalType == 8)
            {
                hasPps = true;
            }

            if (hasSps && hasPps)
            {
                return true;
            }

            pos = FindNextStartCode(data, nalStart + 1, out startCodeLen);
        }

        return false;
    }

    /// <summary>
    /// Extracts all Annex-B SPS (NAL type 7) and PPS (NAL type 8) parameter sets from <paramref name="data"/>,
    /// returning <see langword="null"/> unless both SPS and PPS are present.
    /// </summary>
    public static byte[]? TryExtractSpsPps(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream();
        int pos = FindNextStartCode(data, 0, out int startCodeLen);
        bool hasSps = false;
        bool hasPps = false;

        while (pos >= 0 && pos + startCodeLen < data.Length)
        {
            int nalStart = pos + startCodeLen;
            int nextPos = FindNextStartCode(data, nalStart, out int nextStartCodeLen);
            int nalEnd = nextPos >= 0 ? nextPos : data.Length;

            int nalType = data[nalStart] & 0x1F;
            if (nalType is 7 or 8)
            {
                if (nalType == 7) hasSps = true;
                else hasPps = true;
                stream.Write(data.Slice(pos, nalEnd - pos));
            }

            pos = nextPos;
            startCodeLen = nextStartCodeLen;
        }

        return (hasSps && hasPps && stream.Length > 0) ? stream.ToArray() : null;
    }

    public static int FindNextStartCode(ReadOnlySpan<byte> data, int startIndex, out int startCodeLength)
    {
        for (int i = startIndex; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    startCodeLength = 3;
                    return i;
                }

                if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLength = 4;
                    return i;
                }
            }
        }

        startCodeLength = 0;
        return -1;
    }
}
