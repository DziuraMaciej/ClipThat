using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ClipThat.Engine.Interop;

/// <summary>
/// Shared Media Foundation <see cref="IMFTransform"/> interop helpers used by both the H.264 video
/// encoder (<see cref="Encoding.MfH264Encoder"/>) and the AAC audio encoder
/// (<see cref="Audio.DualAudioCaptureService"/>).
/// </summary>
internal static class MediaFoundationMftHelper
{
    /// <summary>
    /// Wraps a native <c>IMFActivate**</c> array returned by <c>MFTEnumEx</c>/<c>MFTEnum2</c> into
    /// managed <see cref="IMFActivate"/> instances, releasing any remaining native pointers if wrapping fails,
    /// and always frees the outer CoTaskMem buffer.
    /// </summary>
    public static void UnwrapActivatePointerArray(
        nint activatesPtr,
        uint count,
        List<IMFActivate> destination)
    {
        try
        {
            if (activatesPtr == nint.Zero || count == 0)
            {
                return;
            }

            int wrappedCount = 0;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    nint itemPtr = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                    if (itemPtr != nint.Zero)
                    {
                        destination.Add(new IMFActivate(itemPtr));
                    }

                    wrappedCount = i + 1;
                }
            }
            catch
            {
                DisposeCandidateList(destination, excludeIndex: -1);
                destination.Clear();
                for (int i = wrappedCount; i < count; i++)
                {
                    nint remainingPtr = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                    if (remainingPtr != nint.Zero)
                    {
                        Marshal.Release(remainingPtr);
                    }
                }

                throw;
            }
        }
        finally
        {
            if (activatesPtr != nint.Zero)
            {
                Marshal.FreeCoTaskMem(activatesPtr);
            }
        }
    }

    public static void DisposeCandidateList(List<IMFActivate> candidates, int excludeIndex)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i != excludeIndex)
            {
                EngineDisposeHelper.SafeDispose(candidates[i]);
            }
        }
    }

    public static (int InputStreamId, int OutputStreamId) ResolveStreamIds(IMFTransform transform)
    {
        int[] inputIds = new int[1];
        int[] outputIds = new int[1];

        try
        {
            transform.GetStreamIDs(1, inputIds, 1, outputIds);
            return (inputIds[0], outputIds[0]);
        }
        catch (SharpGenException)
        {
            // E_NOTIMPL is standard for fixed-stream MFTs, meaning stream IDs default to 0.
            return (0, 0);
        }
    }

    public static int QueryComRefCount(nint comPtr)
    {
        Marshal.AddRef(comPtr);
        return Marshal.Release(comPtr);
    }

    public static void CleanupReusableOutputAlias(
        IMFSample? reusableSample,
        nint reusableSamplePtr,
        IMFSample? returnedSample)
    {
        if (returnedSample is null || ReferenceEquals(returnedSample, reusableSample))
        {
            return;
        }

        if (reusableSamplePtr != nint.Zero && returnedSample.NativePointer == reusableSamplePtr)
        {
            DetachComAliasWithoutRelease(returnedSample);
        }
        else
        {
            EngineDisposeHelper.SafeDispose(returnedSample);
        }
    }

    public static void DisposeProcessOutputSamples(
        IMFSample? callerAllocatedSample,
        nint callerSamplePtr,
        IMFSample? returnedSample)
    {
        if (returnedSample is not null)
        {
            if (callerAllocatedSample is not null)
            {
                if (ReferenceEquals(returnedSample, callerAllocatedSample))
                {
                    EngineDisposeHelper.SafeDispose(callerAllocatedSample);
                }
                else if (callerSamplePtr != nint.Zero && returnedSample.NativePointer == callerSamplePtr)
                {
                    // Vortice OutputDataBuffer.__MarshalFrom wraps the existing native IMFSample* in a second
                    // managed wrapper without AddRef(). Detach the alias and dispose the caller wrapper once.
                    DetachComAliasWithoutRelease(returnedSample);
                    EngineDisposeHelper.SafeDispose(callerAllocatedSample);
                }
                else
                {
                    // MFT replaced OutputDataBuffer.Sample with a distinct native sample: dispose both.
                    EngineDisposeHelper.SafeDispose(returnedSample);
                    EngineDisposeHelper.SafeDispose(callerAllocatedSample);
                }
            }
            else
            {
                EngineDisposeHelper.SafeDispose(returnedSample);
            }
        }
        else if (callerAllocatedSample is not null)
        {
            EngineDisposeHelper.SafeDispose(callerAllocatedSample);
        }
    }

    public static void DetachComAliasWithoutRelease(ComObject alias)
    {
        try
        {
            alias.NativePointer = nint.Zero;
        }
        catch
        {
            // Best-effort pointer detachment.
        }
        finally
        {
            GC.SuppressFinalize(alias);
        }
    }
}
