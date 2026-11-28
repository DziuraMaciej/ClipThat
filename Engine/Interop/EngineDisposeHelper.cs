using System.Diagnostics;

namespace ClipThat.Engine.Interop;

/// <summary>
/// Best-effort disposal helper for COM/D3D11/Media Foundation/audio engine objects during teardown.
/// </summary>
internal static class EngineDisposeHelper
{
    public static void SafeDispose(IDisposable? disposable, string? debugContext = null)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(debugContext))
            {
                Debug.WriteLine($"{debugContext}: {ex.Message}");
            }
        }
    }
}
