using System;
using System.IO;

namespace Se7enPro.Services;

internal static class FileCacheHelper
{

    public static bool IsCachedCopyUpToDate(string source, string cached)
    {
        try
        {
            if (!File.Exists(cached)) return false;

            var srcInfo = new FileInfo(source);
            var dstInfo = new FileInfo(cached);

            if (srcInfo.Length != dstInfo.Length) return false;

            var srcMtime = srcInfo.LastWriteTimeUtc;
            var dstMtime = dstInfo.LastWriteTimeUtc;
            var delta = (srcMtime - dstMtime).Duration();
            return delta < TimeSpan.FromSeconds(2);
        }
        catch
        {
            return false;
        }
    }
}
