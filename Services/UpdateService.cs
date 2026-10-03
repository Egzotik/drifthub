// Проверка обновлений при старте: manifest.json из гита (raw).
// - Новая версия exe  -> скачать из GitHub-релиза, проверить sha256, заменить себя и перезапуститься.
// - Новые/обновлённые споты -> тихо докачать в spots/<id>/ в фоне.
// Локальный spot.json с "source":"feed" считается управляемым фидом;
// ручные папки без этой метки никогда не удаляются и не затираются,
// пока их версия не ниже фидовой (ручной ymap с тем же именем не трогаем,
// если локальной spot.json нет вообще — считаем ручным и пропускаем).
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DriftHub.Services;

public sealed record UpdateCheckResult(bool AppUpdated, string NewVersion, string NewExePath, int SpotsAdded, int SpotsUpdated);

public static class UpdateService
{
    private const string DefaultRepo = "Egzotik/drifthub";
    private static string FeedUrl
    {
        get
        {
            try
            {
                var overridePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DriftHub", "feed_url.txt");
                if (File.Exists(overridePath))
                {
                    var custom = File.ReadAllText(overridePath).Trim();
                    if (custom.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return custom;
                }
            }
            catch { }
            return $"https://raw.githubusercontent.com/{DefaultRepo}/main/feed/manifest.json";
        }
    }

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DriftHub-Updater/1.0");
    }

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static async Task<UpdateCheckResult> CheckAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        // Жёсткий общий таймаут поверх HttpClient.Timeout — проверка никогда не висит вечно.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        ct = timeout.Token;

        log?.Invoke("Обновления: проверяю " + FeedUrl);
        byte[] raw;
        try
        {
            raw = await Http.GetByteArrayAsync(FeedUrl, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            log?.Invoke("Обновления: фид пока пуст, работаю офлайн.");
            return new(false, CurrentVersion, "", 0, 0);
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("Обновления: превышено время ожидания, работаю офлайн.");
            return new(false, CurrentVersion, "", 0, 0);
        }
        catch (Exception ex)
        {
            log?.Invoke("Обновления: нет связи с сервером (" + ex.Message + "), работаю офлайн.");
            return new(false, CurrentVersion, "", 0, 0);
        }

        using var doc = JsonDocument.Parse(StripBom(raw));
        var root = doc.RootElement;

        int added = 0, updated = 0;
        if (root.TryGetProperty("spots", out var spots) && spots.ValueKind == JsonValueKind.Array)
            (added, updated) = await SyncSpotsAsync(spots, log, ct);

        string newVersion = root.TryGetProperty("appVersion", out var av) ? av.GetString() ?? "" : "";
        if (IsNewer(newVersion, CurrentVersion)
            && root.TryGetProperty("appUrl", out var au)
            && !string.IsNullOrWhiteSpace(au.GetString()))
        {
            string url = au.GetString()!;
            string? wantSha = root.TryGetProperty("appSha256", out var sh) ? sh.GetString() : null;
            log?.Invoke($"Обновления: найдена версия {newVersion}, скачиваю...");
            string tmp = Path.Combine(Path.GetTempPath(), "DriftHub.new.exe");
            await DownloadToFileAsync(url, tmp, ct);
            if (!string.IsNullOrWhiteSpace(wantSha) && !ShaMatches(tmp, wantSha!))
            {
                File.Delete(tmp);
                throw new InvalidDataException("SHA256 нового exe не сошёлся, обновление отменено.");
            }
            log?.Invoke($"Обновления: версия {newVersion} скачана, перезапускаюсь.");
            return new(true, newVersion, tmp, added, updated);
        }

        if (added + updated > 0) log?.Invoke($"Обновления: споты — новых: {added}, обновлено: {updated}.");
        else log?.Invoke("Обновления: всё актуально.");
        return new(false, CurrentVersion, "", added, updated);
    }

    // Скачанный exe подменяет текущий через bat-скрипт (текущий файл заблокирован, пока процесс жив).
    public static void LaunchUpdaterAndRestart(string newExePath)
    {
        string cur = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Не найден путь текущего exe.");
        int pid = Environment.ProcessId;
        string bat = Path.Combine(Path.GetTempPath(), "drifthub-update.bat");
        string script =
            "@echo off\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
            "if not errorlevel 1 ( timeout /t 1 /nobreak >NUL & goto wait )\r\n" +
            $"move /Y \"{newExePath}\" \"{cur}\" >NUL\r\n" +
            $"start \"\" \"{cur}\"\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(bat, script);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        });
    }

    private static async Task<(int added, int updated)> SyncSpotsAsync(JsonElement spots, Action<string>? log, CancellationToken ct)
    {
        int added = 0, updated = 0;
        foreach (var s in spots.EnumerateArray())
        {
            string id = s.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(id)) continue;
            id = id.Trim();
            string dir = Path.Combine(SpotsCatalog.SpotsRoot, id);
            int remoteVer = s.TryGetProperty("version", out var vEl) && vEl.TryGetInt32(out int vv) ? vv : 1;
            int localVer = ReadLocalSpotVersion(dir);
            bool exists = localVer >= 0;
            if (exists && localVer >= remoteVer) continue; // актуально или ручной новее

            string? ymapUrl = s.TryGetProperty("ymapUrl", out var yu) ? yu.GetString() : null;
            if (string.IsNullOrWhiteSpace(ymapUrl)) continue;
            string? wantYmapSha = s.TryGetProperty("ymapSha256", out var ys) ? ys.GetString() : null;

            try
            {
                Directory.CreateDirectory(dir);
                foreach (var stale in Directory.GetFiles(dir, "*.download")) File.Delete(stale);
                byte[] ymap = await Http.GetByteArrayAsync(ymapUrl, ct);
                if (ymap.Length < 16 || BitConverter.ToUInt32(ymap, 0) == 0x52504637)
                    throw new InvalidDataException("битый ymap (RPF вместо чистого ymap).");
                string fileName = s.TryGetProperty("file", out var fEl) && !string.IsNullOrWhiteSpace(fEl.GetString())
                    ? fEl.GetString()! : id + ".ymap";
                string tmpY = Path.Combine(dir, fileName + ".download");
                await File.WriteAllBytesAsync(tmpY, ymap, ct);
                if (!string.IsNullOrWhiteSpace(wantYmapSha) && !ShaMatches(tmpY, wantYmapSha!))
                {
                    File.Delete(tmpY);
                    throw new InvalidDataException("SHA256 ymap не сошёлся.");
                }
                // чистим старый ymap с тем же именем записи, кладём новый
                foreach (var old in Directory.GetFiles(dir, "*.ymap")) File.Delete(old);
                File.Move(tmpY, Path.Combine(dir, fileName), overwrite: true);

                if (s.TryGetProperty("previewUrl", out var pu) && !string.IsNullOrWhiteSpace(pu.GetString()))
                {
                    try
                    {
                        byte[] img = await Http.GetByteArrayAsync(pu.GetString()!, ct);
                        string? wantImgSha = s.TryGetProperty("previewSha256", out var ps) ? ps.GetString() : null;
                        string ext = Path.GetExtension(pu.GetString()!.Split('?')[0]);
                        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                        string tmpI = Path.Combine(dir, "preview.download");
                        await File.WriteAllBytesAsync(tmpI, img, ct);
                        if (!string.IsNullOrWhiteSpace(wantImgSha) && !ShaMatches(tmpI, wantImgSha!))
                            File.Delete(tmpI);
                        else
                        {
                            foreach (var old in Directory.GetFiles(dir, "preview*")
                                         .Where(f => !f.EndsWith(".download", StringComparison.OrdinalIgnoreCase)))
                                File.Delete(old);
                            File.Move(tmpI, Path.Combine(dir, "preview" + ext), overwrite: true);
                        }
                    }
                    catch (Exception ex) { log?.Invoke($"Обновления: превью {id} не скачалось ({ex.Message})."); }
                }

                var meta = new
                {
                    id,
                    name = s.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? id : id,
                    description = s.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "",
                    ymap = fileName,
                    version = remoteVer,
                    source = "feed"
                };
                await File.WriteAllTextAsync(Path.Combine(dir, "spot.json"),
                    JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), ct);
                if (exists) updated++; else added++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Обновления: спот {id} пропущен ({ex.Message}).");
            }
        }
        return (added, updated);
    }

    // -1 = нет локального spot.json (ручная папка или пусто), иначе версия (0 если без поля).
    private static int ReadLocalSpotVersion(string dir)
    {
        string jp = Path.Combine(dir, "spot.json");
        if (!File.Exists(jp)) return Directory.Exists(dir) && Directory.GetFiles(dir, "*.ymap").Any() ? int.MaxValue : -1;
        try
        {
            using var d = JsonDocument.Parse(TextFiles.ReadAllText(jp));
            // ручные споты (без метки фида) не трогаем никогда
            if (d.RootElement.TryGetProperty("source", out var src) &&
                !string.Equals(src.GetString(), "feed", StringComparison.OrdinalIgnoreCase))
                return int.MaxValue;
            if (d.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out int vv))
                return vv;
            return 0;
        }
        catch { return 0; }
    }

    private static byte[] StripBom(byte[] raw) =>
        raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? raw[3..] : raw;

    private static bool IsNewer(string remote, string current)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        if (Version.TryParse(Normalize(remote), out var r) && Version.TryParse(Normalize(current), out var c))
            return r > c;
        return !string.Equals(remote, current, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
        var parts = v.Split('.');
        while (parts.Length < 3)
        {
            v += ".0";
            parts = v.Split('.');
        }
        return v;
    }

    private static async Task DownloadToFileAsync(string url, string dest, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var net = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(dest);
        await net.CopyToAsync(fs, ct);
    }

    private static bool ShaMatches(string path, string wantHex)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        string got = Convert.ToHexString(sha.ComputeHash(fs));
        return string.Equals(got, wantHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
