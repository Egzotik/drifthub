// Base mapping + YMAP spots installer. Everything through our app, no CodeWalker:
// patchday18ng/dlc.rpf <- content.xml @root, drift.rpf + ymap.rpf @x64/levels/gta5.
// Spots = individual clean .ymap files inside the nested ymap.rpf (several at once).
//
// NOTE on game archive lock: original update/ archives use NG crypto whose keys
// can no longer be extracted from GTA 1.0.3889.0 (verified: full-exe scan finds
// 0/101 keys, 0/272 tables). NG tables come from CodeWalker magic.dat, which is
// enough to READ the original dlc.rpf, but our NG *write* path produces a broken
// TOC (verified: EncryptNG->DecryptNG != identity). So the hub keeps every
// original entry, adds content.xml + drift.rpf + ymap.rpf (+spots), and saves the
// archive as OPEN — the same bytes CodeWalker/OpenIV write for modded files.
// Original file is backed up before the first write and can be restored.
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DriftHub.Rpf;

namespace DriftHub.Services;

public static class MappingStore
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string BaseDir => Path.Combine(AppDir, "File");
    public static string SpotsHint =>
        "Положи чистые .ymap спотов в папку spots/<имя>/ вместе с preview.jpg";
    public static string DlcRpfPath(string gta) =>
        Path.Combine(gta, @"update\x64\dlcpacks\patchday18ng\dlc.rpf");
    public static string BackupPath(string gta)
    {
        using var sha = SHA256.Create();
        var id = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(gta))))[..12];
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(folder, "DriftHub", "Backups", $"{Path.GetFileName(gta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}-{id}.dlc.rpf");
    }

    private static string LegacyBackupPath(string gta) => DlcRpfPath(gta) + ".drifthub.bak";

    private static bool HasBackup(string gta)
    {
        if (File.Exists(BackupPath(gta))) return true;
        var legacy = LegacyBackupPath(gta);
        if (!File.Exists(legacy)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath(gta))!);
        File.Copy(legacy, BackupPath(gta));
        File.Delete(legacy);
        return true;
    }

    public sealed record BaseStatus(bool Installed, string Detail, bool BackupExists);

    public static BaseStatus GetBaseStatus(string gta)
    {
        string dlc = DlcRpfPath(gta);
        bool bak = HasBackup(gta);
        if (!File.Exists(dlc)) return new(false, "dlc.rpf не найден", bak);
        try
        {
            KeyStore.Ensure(gta);
            var arc = RpfArchive.Load(dlc);
            var files = arc.ListFiles();
            bool ok = files.Contains("content.xml")
                && files.Contains("x64/levels/gta5/drift.rpf")
                && files.Contains("x64/levels/gta5/ymap.rpf");
            if (!ok) return new(false, "в архиве нет нашей базы", bak);
            var cur = arc.Root.Files.First(f => f.Name == "content.xml").Data;
            var want = File.ReadAllBytes(Path.Combine(BaseDir, "content.xml"));
            return new(true, cur.SequenceEqual(want) ? "база от DriftHub стоит" : "база стоит (content.xml другой)", bak);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("not RPF7"))
        {
            return new(false, "файл не RPF", bak);
        }
        catch (Exception ex) when (ex.Message.Contains("NG") || ex.Message.Contains("crypto") || ex is InvalidDataException)
        {
            return new(false, "оригинальный архив игры (закрыт). Нажми «Установить базу».", bak);
        }
        catch (Exception ex) { return new(false, "ошибка чтения: " + ex.Message, bak); }
    }

    public static HashSet<string> GetInstalledSpots(string gta)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var arc = RpfArchive.Load(DlcRpfPath(gta));
            if (arc.Nested.TryGetValue("x64/levels/gta5/ymap.rpf", out var ymap))
                foreach (var f in ymap.ListFiles())
                    if (f.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase))
                        set.Add(f.ToLowerInvariant());
        }
        catch { }
        return set;
    }

    public static void InstallBase(string gta, Action<string> log, Action<int>? progress = null)
    {
        progress?.Invoke(5);
        string dlc = DlcRpfPath(gta);
        if (!File.Exists(dlc)) throw new FileNotFoundException("Не найден " + dlc);
        foreach (var f in new[] { "content.xml", "drift.rpf", "ymap.rpf" })
            if (!File.Exists(Path.Combine(BaseDir, f)))
                throw new FileNotFoundException($"Нет базового файла File/{f}");

        if (!HasBackup(gta))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath(gta))!);
            File.Copy(dlc, BackupPath(gta));
            log("Оригинальный dlc.rpf сохранён в резервное хранилище DriftHub.");
        }

        progress?.Invoke(30);
        KeyStore.Ensure(gta, log);
        var arc = RpfArchive.Load(dlc);
        RpfArchive.Upsert(arc.Root, "content.xml", File.ReadAllBytes(Path.Combine(BaseDir, "content.xml")));
        var dir = arc.FindDir("x64/levels/gta5", create: true)!;
        RpfArchive.Upsert(dir, "drift.rpf", File.ReadAllBytes(Path.Combine(BaseDir, "drift.rpf")));
        // Оригинальный patchday18ng НЕ содержит ymap.rpf — создаём из нашей базы File/ymap.rpf.
        var baseYmap = RpfArchive.Load(Path.Combine(BaseDir, "ymap.rpf"));
        var yEntry = dir.Files.FirstOrDefault(f => f.Name.Equals("ymap.rpf", StringComparison.OrdinalIgnoreCase));
        if (yEntry == null)
        {
            RpfArchive.Upsert(dir, "ymap.rpf", baseYmap.Save());
            log("Создан новый x64/levels/gta5/ymap.rpf из базы.");
        }
        else
        {
            var yArc = RpfArchive.Load(yEntry.Data, "ymap.rpf");
            foreach (var f in baseYmap.Root.Files) RpfArchive.Upsert(yArc.Root, f.Name, f.Data);
            yEntry.Data = yArc.Save();
        }

        progress?.Invoke(70);
        // NG-шифрование при записи сейчас даёт битый TOC (проверено: EncryptNG->DecryptNG != identity),
        // поэтому сохраняем как OPEN — так же пишут модовые архивы CodeWalker/OpenIV.
        arc.Encryption = RpfEncryption.OPEN;
        byte[] rebuilt = arc.Save();
        var check = RpfArchive.Load(rebuilt, "dlc.rpf"); // verify before touching game
        var cf = check.ListFiles();
        if (!cf.Contains("content.xml") || !cf.Contains("x64/levels/gta5/drift.rpf") || !cf.Contains("x64/levels/gta5/ymap.rpf"))
            throw new InvalidDataException("Проверка сборки не прошла, запись отменена.");
        progress?.Invoke(85);
        File.WriteAllBytes(dlc, rebuilt);
        progress?.Invoke(100);
        log("Файлы добавлены в существующий dlc.rpf. Остальная структура архива сохранена.");
    }

    public static void RestoreBackup(string gta, Action<string> log)
    {
        string bak = File.Exists(BackupPath(gta)) ? BackupPath(gta) : LegacyBackupPath(gta);
        if (!File.Exists(bak)) throw new FileNotFoundException("Бэкапа нет: " + bak);
        File.Copy(bak, DlcRpfPath(gta), overwrite: true);
        log("Вернул оригинальный файл из бэкапа.");
    }

    public static void SetSpot(string gta, string entryName, byte[]? ymapBytes, bool install, Action<string> log)
    {
        KeyStore.Ensure(gta, log);
        var arc = RpfArchive.Load(DlcRpfPath(gta));
        var dir = arc.FindDir("x64/levels/gta5") ?? throw new InvalidDataException("Сначала установи базу.");
        var yEntry = dir.Files.FirstOrDefault(f => f.Name.Equals("ymap.rpf", StringComparison.OrdinalIgnoreCase));
        if (yEntry == null)
        {
            var baseYmap = RpfArchive.Load(Path.Combine(BaseDir, "ymap.rpf"));
            RpfArchive.Upsert(dir, "ymap.rpf", baseYmap.Save());
            yEntry = dir.Files.First(f => f.Name.Equals("ymap.rpf", StringComparison.OrdinalIgnoreCase));
            log("ymap.rpf отсутствовал — создан из базы.");
        }
        var yArc = RpfArchive.Load(yEntry.Data, "ymap.rpf");
        if (install)
        {
            if (ymapBytes == null || ymapBytes.Length < 16)
                throw new InvalidDataException("Пустой .ymap.");
            if (BitConverter.ToUInt32(ymapBytes, 0) == 0x52504637)
                throw new InvalidDataException("Это RPF, а нужен чистый .ymap.");
            RpfArchive.Upsert(yArc.Root, entryName.ToLowerInvariant(), ymapBytes);
            log($"Спот '{entryName}' добавлен.");
        }
        else
        {
            log(RpfArchive.Remove(yArc.Root, entryName.ToLowerInvariant())
                ? $"Спот '{entryName}' убран." : $"Спот '{entryName}' и так отсутствует.");
        }
        yEntry.Data = yArc.Save();
        arc.Encryption = RpfEncryption.OPEN; // см. InstallBase: NG-запись даёт битый TOC
        byte[] rebuilt = arc.Save();
        _ = RpfArchive.Load(rebuilt, "dlc.rpf"); // verify
        File.WriteAllBytes(DlcRpfPath(gta), rebuilt);
    }
}
