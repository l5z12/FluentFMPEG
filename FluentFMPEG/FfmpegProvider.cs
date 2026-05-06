using System.Threading.Tasks;

namespace FluentFMPEG
{
    // Bundled libav* DLLs ship with the app via Sdcb.FFmpeg.runtime.windows-*
    // NuGet packages, so there's no first-run download — just a probe of the
    // P/Invoke surface to surface DLL load failures early.
    internal static class FfmpegProvider
    {
        public static string? LibVersion { get; private set; }
        public static bool AreLibsReady { get; private set; }

        public static Task<bool> InitializeLibsAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    LibVersion = Sdcb.FFmpeg.Raw.ffmpeg.av_version_info();
                    AreLibsReady = true;
                    return true;
                }
                catch
                {
                    AreLibsReady = false;
                    return false;
                }
            });
        }
    }
}
