using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Karmac
{
    // A display/reconnect hint only. Connection must revalidate the Windows bond.
    internal sealed class SavedAirPods
    {
        public string ContainerId { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string BluetoothId { get; set; }
        public int ModelId { get; set; }
        internal static SavedAirPods FromTarget(AudioTarget target)
        {
            Guid id;
            if (target == null || target.RequiresPairingCheck || !Guid.TryParse(target.ContainerId, out id) || id == Guid.Empty) return null;
            return new SavedAirPods { ContainerId = id.ToString(), Name = target.Name, Address = target.Address,
                BluetoothId = target.BluetoothId, ModelId = target.ModelId };
        }
        internal AudioTarget Restore(string favorite)
        {
            if (!DeviceDiscovery.SameGuid(ContainerId, favorite) || String.IsNullOrWhiteSpace(Name)) return null;
            return new AudioTarget { ContainerId = ContainerId, Name = Name, Address = DeviceDiscovery.NormalizeAddress(Address),
                BluetoothId = BluetoothId, ModelId = AirPodsModels.IsKnown(ModelId) ? ModelId : 0, IsApple = true, RequiresPairingCheck = true };
        }
    }

    internal sealed class Preferences
    {
        public string Favorite { get; set; }
        public SavedAirPods SavedPair { get; set; }
        public bool RouteAudio { get; set; }
        public bool CloseToTray { get; set; }
        public bool Hotkey { get; set; }
        public bool PowerOffBluetoothOnDisconnect { get; set; }
        public Preferences() { RouteAudio = true; CloseToTray = true; Hotkey = true; PowerOffBluetoothOnDisconnect = true; }
        internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Karmac");
        // Read the old portable app's data as a fallback; all future writes use Karmac.
        internal static readonly string LegacyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenConnect");
        internal static Preferences Load()
        {
            return LoadFromFolders(Folder, LegacyFolder);
        }
        internal static Preferences LoadFromFolders(string folder, string legacyFolder)
        {
            try
            {
                string path = Path.Combine(folder, "settings.json");
                if (!File.Exists(path)) path = Path.Combine(legacyFolder, "settings.json");
                if (File.Exists(path)) return new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(path)) ?? new Preferences();
            }
            catch (Exception ex) { Log.Write("Load preferences: " + ex.Message); }
            return new Preferences();
        }
        internal void Save()
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "settings.json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
    internal static class Log
    {
        private static readonly object gate = new object();
        internal static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Preferences.Folder);
                    string path = Path.Combine(Preferences.Folder, "app.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                        File.WriteAllText(path, "");
                    File.AppendAllText(path, DateTime.UtcNow.ToString("u") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
