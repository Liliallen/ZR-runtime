using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// Open NVDEC clock sessions (mm:u Initialize/Finalize with module NvDec). Games' movie players (nn::movie) open one
    /// for the duration of a video, so a non-zero count means a video is playing. Read by Ryujinx.HLE's BotwVideo.
    /// </summary>
    public static class VideoDecodeSessions
    {
        private static int _open;

        public static int Open => Volatile.Read(ref _open);

        public static void OnOpened() => Interlocked.Increment(ref _open);

        public static void OnClosed()
        {
            int value;

            do
            {
                value = Volatile.Read(ref _open);
            }
            while (value > 0 && Interlocked.CompareExchange(ref _open, value - 1, value) != value);
        }

        /// <summary>Forgets sessions of a previous guest process.</summary>
        public static void Reset() => Volatile.Write(ref _open, 0);
    }
}
