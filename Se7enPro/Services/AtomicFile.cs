using System;
using System.IO;

namespace Se7enPro.Services;

internal static class AtomicFile
{

    internal static void Install(string sourcePath, string destinationPath)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var staged = destinationPath + ".new." + Guid.NewGuid().ToString("N");
        File.Copy(sourcePath, staged, overwrite: true);
        SwapIntoPlace(staged, destinationPath);
    }

    internal static void WriteAllText(string destinationPath, string contents)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var staged = destinationPath + ".new." + Guid.NewGuid().ToString("N");

        using (var stream = new FileStream(
                   staged, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        SwapIntoPlace(staged, destinationPath);
    }

    private static void SwapIntoPlace(string staged, string destinationPath)
    {
        try
        {
            if (!File.Exists(destinationPath))
            {

                File.Move(staged, destinationPath);
                return;
            }

            var backup = destinationPath + ".bak";
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }

            File.Replace(staged, destinationPath, backup, ignoreMetadataErrors: true);
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
        }
        catch
        {

            var parked = destinationPath + ".old." + Guid.NewGuid().ToString("N");
            var parkedOk = false;
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Move(destinationPath, parked);
                    parkedOk = true;
                }
                File.Move(staged, destinationPath);
                if (parkedOk)
                {
                    try { File.Delete(parked); } catch { }
                }
            }
            catch
            {

                if (parkedOk && !File.Exists(destinationPath))
                {
                    try { File.Move(parked, destinationPath); } catch { }
                }
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                throw;
            }
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }
}
