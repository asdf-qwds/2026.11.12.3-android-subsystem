using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace AndroidSubsystem;

internal static class Assets
{
    /// <summary>读取打包进 exe 的预览图（AndroidSubsystem.Assets.skin1.png）。</summary>
    public static Image Load(string fileName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("AndroidSubsystem.Assets." + fileName);
            if (stream == null) return new Bitmap(1, 1);

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            return Image.FromStream(ms);
        }
        catch
        {
            return new Bitmap(1, 1);
        }
    }
}
