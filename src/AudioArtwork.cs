using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Karmac
{
    internal sealed class ArtworkImage
    {
        internal readonly ImageSource Source;
        internal readonly string Description;
        internal ArtworkImage(ImageSource source, string description) { Source = source; Description = description; }
    }

    // Artwork never participates in discovery, Bluetooth power, pairing or audio routing.
    // Keep proprietary/user-provided imagery outside the executable and release archive.
    internal static class AudioArtwork
    {
        private const long MaximumBytes = 4 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>();
        private static readonly ConcurrentDictionary<string, Lazy<ArtworkImage>> Bundled = new ConcurrentDictionary<string, Lazy<ArtworkImage>>();
        private static int generation;
        private sealed class CacheEntry { internal DateTime Created; internal Task<ArtworkImage> Task; }

        internal static void Invalidate()
        {
            lock (Gate) { Cache.Clear(); generation++; }
        }

        internal static Task<ArtworkImage> LoadAsync(string modelKey, string bluetoothId)
        {
            if (!ValidKey(modelKey)) return Task.FromResult<ArtworkImage>(null);
            string key = modelKey + "|" + (bluetoothId ?? String.Empty);
            lock (Gate)
            {
                CacheEntry previous;
                if (Cache.TryGetValue(key, out previous) &&
                    (!previous.Task.IsCompleted || DateTime.UtcNow - previous.Created < TimeSpan.FromSeconds(60))) return previous.Task;
                // Both file decoding and Windows metadata requests run away from the UI thread.
                var entry = new CacheEntry { Created = DateTime.UtcNow };
                int requestedGeneration = generation;
                entry.Task = Task.Run(async () =>
                {
                    var image = LoadLocal(modelKey) ?? LoadBundled(modelKey) ?? await LoadWindowsAsync(bluetoothId).ConfigureAwait(false);
                    lock (Gate) { if (requestedGeneration != generation) return null; }
                    return image;
                });
                if (Cache.Count >= 128) Cache.Clear();
                Cache[key] = entry;
                return entry.Task;
            }
        }

        private static bool ValidKey(string key)
        {
            return !String.IsNullOrWhiteSpace(key) && key.Length <= 80 &&
                key.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');
        }

        // Generated family renders are embedded for portable, offline display. Decode
        // once per family on the artwork worker; no Bluetooth task awaits this work.
        internal static ArtworkImage LoadBundled(string modelKey)
        {
            string resource;
            switch (modelKey)
            {
                case "airpods-1": case "airpods-2":
                    resource = "Headphones.airpods-long-stem.png"; break;
                case "airpods-3": case "airpods-4": case "airpods-4-anc":
                case "airpods-5": case "airpods-5-wireless":
                    resource = "Headphones.airpods-short-stem.png"; break;
                case "airpods-pro": case "airpods-pro-2-lightning": case "airpods-pro-2-usbc":
                    resource = "Headphones.airpods-pro.png"; break;
                case "airpods-max-lightning": case "airpods-max-usbc": case "airpods-max-2":
                    resource = "Headphones.max.png"; break;
                case "beats-studio-3": case "beats-studio-pro": case "beats-360":
                    resource = "Headphones.studio.png"; break;
                case "beats-solo-3": case "beats-solo-4": case "beats-solo-pro":
                    resource = "Headphones.solo.png"; break;
                case "beats-studio-buds": case "beats-studio-buds-plus": case "beats-solo-buds":
                    resource = "Headphones.beats-buds.png"; break;
                case "beats-fit-pro":
                    resource = "Headphones.beats-fit-pro.png"; break;
                case "powerbeats-pro": case "powerbeats-pro-2":
                    resource = "Headphones.powerbeats-pro.png"; break;
                case "beats-x": case "beats-flex":
                    resource = "Headphones.beats-neckband.png"; break;
                case "powerbeats-3": case "powerbeats-4":
                    resource = "Headphones.powerbeats-cabled.png"; break;
                default: return null;
            }
            return Bundled.GetOrAdd(resource, name => new Lazy<ArtworkImage>(() =>
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                    {
                        if (stream == null) return null;
                        var source = Decode(stream);
                        return source == null ? null : new ArtworkImage(source,
                            "Original AI-generated family illustration; example finish, not an official product photo");
                    }
                }
                catch (Exception ex) { Log.Write("Bundled artwork unavailable: " + ex.GetType().Name); return null; }
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private static ArtworkImage LoadLocal(string modelKey)
        {
            foreach (string folder in new[] { Preferences.Folder, Preferences.LegacyFolder })
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg" })
            {
                try
                {
                    string path = Path.Combine(folder, "Artwork", modelKey + extension);
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length == 0 || file.Length > MaximumBytes) continue;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var source = Decode(stream);
                        if (source != null) return new ArtworkImage(source, "Locally supplied image · " + modelKey);
                    }
                }
                catch (Exception ex) { Log.Write("Local artwork unavailable: " + ex.GetType().Name); }
            }
            return null;
        }

        private static async Task<ArtworkImage> LoadWindowsAsync(string bluetoothId)
        {
            if (String.IsNullOrWhiteSpace(bluetoothId)) return null;
            try
            {
                string[] properties = { "System.Devices.Icon", "System.Devices.Aep.ContainerId" };
                var device = await WinRtTask.Wait(DeviceInformation.CreateFromIdAsync(bluetoothId,
                    properties, DeviceInformationKind.AssociationEndpoint), 2000).ConfigureAwait(false);
                if (device == null || !device.Pairing.IsPaired) return null;
                var source = await WindowsImageAsync(device).ConfigureAwait(false);
                if (source != null) return source;
                object container;
                Guid containerId;
                if (device.Properties.TryGetValue("System.Devices.Aep.ContainerId", out container) &&
                    Guid.TryParse(Convert.ToString(container), out containerId) && containerId != Guid.Empty)
                {
                    var info = await WinRtTask.Wait(DeviceInformation.CreateFromIdAsync(containerId.ToString("B"),
                        properties, DeviceInformationKind.DeviceContainer), 1500).ConfigureAwait(false);
                    if (info != null) return await WindowsImageAsync(info).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Log.Write("Windows artwork unavailable: " + ex.GetType().Name); }
            return null;
        }

        private static async Task<ArtworkImage> WindowsImageAsync(DeviceInformation device)
        {
            object icon;
            // GetThumbnailAsync can return a generic icon. Only use device metadata imagery;
            // a shell32/Windows device-class glyph must never replace the model illustration.
            if (!device.Properties.TryGetValue("System.Devices.Icon", out icon) || !IsMetadataImage(Convert.ToString(icon))) return null;
            byte[] image;
            using (var thumbnail = await WinRtTask.Wait(device.GetThumbnailAsync(), 1500).ConfigureAwait(false))
                image = await ReadThumbnailAsync(thumbnail).ConfigureAwait(false);
            if (image == null) return null;
            using (var glyph = await WinRtTask.Wait(device.GetGlyphThumbnailAsync(), 1500).ConfigureAwait(false))
            {
                var generic = await ReadThumbnailAsync(glyph).ConfigureAwait(false);
                if (generic != null && image.SequenceEqual(generic)) return null;
            }
            using (var stream = new MemoryStream(image, false))
            {
                var bitmap = Decode(stream);
                return bitmap == null ? null : new ArtworkImage(bitmap, "Windows-provided device image");
            }
        }

        private static bool IsMetadataImage(string icon)
        {
            if (String.IsNullOrWhiteSpace(icon)) return false;
            return icon.IndexOf("DeviceMetadataCache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                icon.IndexOf("DeviceMetadataStore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                icon.StartsWith("dmrc:", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<byte[]> ReadThumbnailAsync(DeviceThumbnail thumbnail)
        {
            if (thumbnail == null || !thumbnail.CanRead || thumbnail.Size == 0 || thumbnail.Size > MaximumBytes) return null;
            using (var reader = new DataReader(thumbnail.GetInputStreamAt(0)))
            {
                uint size = (uint)thumbnail.Size;
                uint read = await WinRtTask.Wait(reader.LoadAsync(size), 1500).ConfigureAwait(false);
                if (read != size) return null;
                var bytes = new byte[size]; reader.ReadBytes(bytes); return bytes;
            }
        }

        private static ImageSource Decode(Stream stream)
        {
            // OnLoad closes the backing file immediately; a frozen source is thread-safe.
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 480; bitmap.StreamSource = stream; bitmap.EndInit();
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || bitmap.PixelHeight > 4096) return null;
            bitmap.Freeze(); return bitmap;
        }
    }
}
