// One-time NG/AES key extraction from the user's own GTA5.exe
// (same approach as CodeWalker: hash-search in exe, then cache locally).
using System.IO;

namespace DriftHub.Rpf;

public static class KeyStore
{
    private static bool _ready;
    private static readonly object _lock = new();

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DriftHub", "keys");

    public static bool IsCached => new[]
    {
        "gtav_aes_key.dat",
        "gtav_ng_key.dat",
        "gtav_ng_decrypt_tables.dat",
        "gtav_ng_encrypt_tables.dat",
        "gtav_ng_encrypt_luts.dat",
        "gtav_hash_lut.dat"
    }.All(file => File.Exists(Path.Combine(CacheDir, file)));

    public static void Ensure(string gtaPath, Action<string>? log = null)
    {
        lock (_lock)
        {
            if (_ready) return;
            if (IsCached)
            {
                LoadCache();
                _ready = true;
                return;
            }
            string exe = Path.Combine(gtaPath, "GTA5.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("GTA5.exe not found: " + exe);
            string? magicPath = FindCodeWalkerMagic();
            if (magicPath != null)
            {
                log?.Invoke("Загружаю NG-таблицы CodeWalker...");
                GTA5Keys.LoadFromMagic(File.ReadAllBytes(exe), File.ReadAllBytes(magicPath), s => log?.Invoke(s));
                SaveCache();
                _ready = true;
                return;
            }
            log?.Invoke("Первый запуск: извлекаю ключи из GTA5.exe (один раз, ~1-3 мин)...");
            byte[] exeData = File.ReadAllBytes(exe);
            GTA5Keys.Generate(exeData, s => log?.Invoke("Ключи: " + s));
            SaveCache();
            _ready = true;
        }
    }

    private static void SaveCache()
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllBytes(Path.Combine(CacheDir, "gtav_aes_key.dat"), GTA5Keys.PC_AES_KEY);
        CryptoIO.WriteNgKeys(Path.Combine(CacheDir, "gtav_ng_key.dat"), GTA5Keys.PC_NG_KEYS);
        CryptoIO.WriteNgTables(Path.Combine(CacheDir, "gtav_ng_decrypt_tables.dat"), GTA5Keys.PC_NG_DECRYPT_TABLES);
        CryptoIO.WriteNgTables(Path.Combine(CacheDir, "gtav_ng_encrypt_tables.dat"), GTA5Keys.PC_NG_ENCRYPT_TABLES);
        CryptoIO.WriteLuts(Path.Combine(CacheDir, "gtav_ng_encrypt_luts.dat"), GTA5Keys.PC_NG_ENCRYPT_LUTs);
        File.WriteAllBytes(Path.Combine(CacheDir, "gtav_hash_lut.dat"), GTA5Keys.PC_LUT);
    }

    private static string? FindCodeWalkerMagic()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CodeWalker"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "CodeWalker"),
            Path.Combine(AppContext.BaseDirectory, "CodeWalker")
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var match = Directory.EnumerateFiles(root, "magic.dat", SearchOption.AllDirectories).FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    private static void LoadCache()
    {
        GTA5Keys.PC_AES_KEY = File.ReadAllBytes(Path.Combine(CacheDir, "gtav_aes_key.dat"));
        GTA5Keys.PC_NG_KEYS = CryptoIO.ReadNgKeys(File.ReadAllBytes(Path.Combine(CacheDir, "gtav_ng_key.dat")));
        GTA5Keys.PC_NG_DECRYPT_TABLES = CryptoIO.ReadNgTables(File.ReadAllBytes(Path.Combine(CacheDir, "gtav_ng_decrypt_tables.dat")));
        GTA5Keys.PC_NG_ENCRYPT_TABLES = CryptoIO.ReadNgTables(File.ReadAllBytes(Path.Combine(CacheDir, "gtav_ng_encrypt_tables.dat")));
        GTA5Keys.PC_NG_ENCRYPT_LUTs = CryptoIO.ReadNgLuts(File.ReadAllBytes(Path.Combine(CacheDir, "gtav_ng_encrypt_luts.dat")));
        GTA5Keys.PC_LUT = File.ReadAllBytes(Path.Combine(CacheDir, "gtav_hash_lut.dat"));
        GTA5Hash.LUT = GTA5Keys.PC_LUT;
    }
}
