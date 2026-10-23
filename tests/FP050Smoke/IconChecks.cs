using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WinGetToolbox;

internal static partial class Program
{
    private const string ApprovedIconHash = "A73C0EDFD9461B4636B4080A3288327561B9E6B23984D64D06755A65A2AF4D8A";
    private static void VerifyIcon(MainWindow window)
    {
        var repo = Directory.GetCurrentDirectory();
        var original = File.ReadAllBytes(Path.Combine(repo, "src", "WinGetToolbox", "Assets", "Toolbox Matrix.ico"));
        Check(Convert.ToHexString(SHA256.HashData(original)) == ApprovedIconHash, "project icon is byte-identical to the approved Toolbox Matrix v02.ico attachment");
        using var embeddedStream = Application.GetResourceStream(new Uri("pack://application:,,,/WinGetToolbox;component/Assets/Toolbox Matrix.ico")).Stream;
        using var copy = new MemoryStream(); embeddedStream.CopyTo(copy);
        Check(original.SequenceEqual(copy.ToArray()), "WPF embedded icon bytes exactly match all approved ICO bytes");
        var count = BitConverter.ToUInt16(original, 4);
        Check(BitConverter.ToUInt16(original, 0) == 0 && BitConverter.ToUInt16(original, 2) == 1 && count > 0, "approved file has a valid ICO directory");
        var frames = Enumerable.Range(0, count).Select(i =>
        {
            var p = 6 + 16 * i;
            var length = checked((int)BitConverter.ToUInt32(original, p + 8));
            var offset = checked((int)BitConverter.ToUInt32(original, p + 12));
            return new { Width = original[p] == 0 ? 256 : (int)original[p], Height = original[p + 1] == 0 ? 256 : (int)original[p + 1],
                Bytes = length, Data = original.AsSpan(offset, length).ToArray() };
        }).ToArray();
        var decoder = new IconBitmapDecoder(new MemoryStream(original), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check(decoder.Frames.Count == count && decoder.Frames.Zip(frames).All(x => x.First.PixelWidth == x.Second.Width && x.First.PixelHeight == x.Second.Height),
            "WPF decodes every original icon frame at its original resolution");
        var exe = Path.Combine(repo, "src", "WinGetToolbox", "bin", "Release", "net10.0-windows", "WinGetToolbox.exe");
        var module = LoadLibraryEx(exe, IntPtr.Zero, 0x22); // data/image resources only; no entry point is executed
        Check(module != IntPtr.Zero, "executable resources load as data without starting the application");
        try
        {
            var groups = new List<IntPtr>();
            EnumResourceName callback = (_, _, name, _) => { groups.Add(name); return true; };
            Check(EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero) && groups.Count == 1, "executable contains one application icon group");
            var group = ResourceBytes(module, new IntPtr(14), groups[0]);
            Check(BitConverter.ToUInt16(group, 4) == count, "native executable icon group has every approved frame");
            for (var i = 0; i < count; i++)
            {
                var p = 6 + 14 * i; var id = BitConverter.ToUInt16(group, p + 12);
                var data = ResourceBytes(module, new IntPtr(3), new IntPtr(id));
                Check(data.SequenceEqual(frames[i].Data) && group[p] == original[6 + 16 * i] && group[p + 1] == original[7 + 16 * i],
                    "native ICO frame " + frames[i].Width + "x" + frames[i].Height + " preserves exact encoded bytes");
            }
            GC.KeepAlive(callback);
        }
        finally { FreeLibrary(module); }
        Call(window, "UpdateTitleIcon");
        var title = (BitmapSource)((Image)window.FindName("TitleAppIcon")).Source;
        var windowIcon = (BitmapSource)window.Icon;
        bool Matches(BitmapSource image) => decoder.Frames.Any(frame => frame.PixelWidth == image.PixelWidth && frame.PixelHeight == image.PixelHeight && Pixels(frame).SequenceEqual(Pixels(image)));
        Check(Matches(title) && Matches(windowIcon), "title toolbar and WPF Window.Icon render original approved frame pixels");
        using (var trayIcon = new System.Drawing.Icon(new MemoryStream(original)))
            Check(trayIcon.Width > 0 && trayIcon.Height > 0, "the shared app-owned tray resource decodes without creating a tray icon");
        File.WriteAllText(Path.Combine(ToolboxPaths.Root, "icon-verification.json"), JsonSerializer.Serialize(new
        {
            ApprovedSha256 = ApprovedIconHash, Bytes = original.Length, FrameCount = count,
            Frames = frames.Select(frame => new { frame.Width, frame.Height, frame.Bytes, Sha256 = Convert.ToHexString(SHA256.HashData(frame.Data)) }),
            NativeFramesEqual = true, WpfResourceEqual = true, WindowAndToolbarPixelsEqual = true,
            NativeTaskbarOrTrayDisplay = "Not launched or visually checked"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelHeight * ((bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8)];
        bitmap.CopyPixels(pixels, pixels.Length / bitmap.PixelHeight, 0); return pixels;
    }
    private static byte[] ResourceBytes(IntPtr module, IntPtr type, IntPtr name)
    {
        var resource = FindResource(module, name, type);
        if (resource == IntPtr.Zero) throw new InvalidDataException("Missing executable icon resource.");
        var data = new byte[checked((int)SizeofResource(module, resource))];
        Marshal.Copy(LockResource(LoadResource(module, resource)), data, 0, data.Length); return data;
    }
    private delegate bool EnumResourceName(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceName callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
}
