using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Karmac.Tests
{
    internal static class ArtworkSmoke
    {
        private static int checks;
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
        [STAThread]
        private static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine(checks + " artwork checks passed."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static async Task Run()
        {
            string key = "artwork-smoke-" + Guid.NewGuid().ToString("N");
            string folder = Path.Combine(Preferences.Folder, "Artwork");
            string path = Path.Combine(folder, key + ".png");
            Directory.CreateDirectory(folder);
            try
            {
                Check(await AudioArtwork.LoadAsync("../escape", null) == null, "Path traversal keys rejected");
                Check(await AudioArtwork.LoadAsync(key, null) == null, "Missing artwork leaves original fallback available");
                byte[] pixels = new byte[24 * 16 * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 80; pixels[i+3] = 255; }
                var bitmap = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 24 * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) encoder.Save(output);
                Check(await AudioArtwork.LoadAsync(key, null) == null, "Cached misses remain stable until refresh");
                AudioArtwork.Invalidate();
                var image = await AudioArtwork.LoadAsync(key, null);
                Check(image != null && image.Source.IsFrozen, "Local model image loads as a frozen cross-thread source");
                Check(image.Description.StartsWith("Locally supplied image"), "User image provenance never claims Apple ownership");
                var again = await AudioArtwork.LoadAsync(key, null);
                Check(Object.ReferenceEquals(image.Source, again.Source), "Repeated display reuses decoded image cache");
                File.WriteAllText(path, "invalid PNG");
                Check(File.ReadAllText(path) == "invalid PNG", "Image decode releases the source file immediately");
                AudioArtwork.Invalidate();
                Check(await AudioArtwork.LoadAsync(key, null) == null, "Corrupt artwork returns to original fallback");
                var checkMetadata = typeof(AudioArtwork).GetMethod("IsMetadataImage", BindingFlags.NonPublic | BindingFlags.Static);
                Check(!(bool)checkMetadata.Invoke(null, new object[] { "@%SystemRoot%\\System32\\DDORes.dll,-3012" }), "Generic Windows class artwork rejected");
                Check((bool)checkMetadata.Invoke(null, new object[] { "C:\\ProgramData\\Microsoft\\Windows\\DeviceMetadataCache\\model\\icon.ico" }), "Manufacturer metadata artwork eligible");
                foreach (int modelId in AirPodsModels.KnownIds)
                {
                    // Pro 3 retains its existing high-resolution embedded render,
                    // exercised directly by the app UI smoke fixture.
                    if (modelId == 0x2027) continue;
                    string model = AirPodsModels.ArtworkKey(modelId);
                    var render = await Task.Run(() => AudioArtwork.LoadBundled(model));
                    var pixelsSource = render == null ? null : render.Source as BitmapSource;
                    Check(pixelsSource != null && pixelsSource.IsFrozen && pixelsSource.PixelWidth == 480,
                        model + " has an embedded, bounded-resolution render usable across threads offline");
                    Check(render.Description.Contains("family illustration") && !render.Description.StartsWith("Windows"),
                        model + " describes the render as an illustration rather than official photography");
                }
                Check(Object.ReferenceEquals(AudioArtwork.LoadBundled("airpods-max-usbc").Source,
                    AudioArtwork.LoadBundled("airpods-max-lightning").Source), "Matching family renders share a decoded image");
                Check(!Object.ReferenceEquals(AudioArtwork.LoadBundled("beats-solo-4").Source,
                    AudioArtwork.LoadBundled("beats-studio-pro").Source), "Solo and Studio have distinct product silhouettes");
                Check(AudioArtwork.LoadBundled("audio-unknown") == null, "Unknown products retain their generic fallback");
            }
            finally { AudioArtwork.Invalidate(); if (File.Exists(path)) File.Delete(path); }
        }
    }
}
