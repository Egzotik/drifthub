using System.IO;
using System.Text.Json;
using DriftHub.Models;

namespace DriftHub.Services;

// spots/<id>/{spot.json, *.ymap, preview.*}
public static class SpotsCatalog
{
    public static string SpotsRoot => Path.Combine(AppContext.BaseDirectory, "spots");

    private sealed class SpotJson
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string description { get; set; } = "";
        public string ymap { get; set; } = "";
    }

    public static List<Spot> Load()
    {
        var root = SpotsRoot;
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        if (!Directory.GetDirectories(root).Any()) CreateReadme(root);

        var list = new List<Spot>();
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
        {
            try
            {
                string id = Path.GetFileName(dir);
                string name = id, desc = "";
                string? ymap = Directory.GetFiles(dir, "*.ymap").FirstOrDefault();
                var jp = Path.Combine(dir, "spot.json");
                if (File.Exists(jp))
                {
                    var raw = JsonSerializer.Deserialize<SpotJson>(TextFiles.ReadAllText(jp),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (raw != null)
                    {
                        if (!string.IsNullOrWhiteSpace(raw.id)) id = raw.id;
                        if (!string.IsNullOrWhiteSpace(raw.name)) name = raw.name;
                        desc = raw.description ?? "";
                        if (!string.IsNullOrWhiteSpace(raw.ymap))
                        {
                            var cand = Path.Combine(dir, raw.ymap);
                            if (File.Exists(cand)) ymap = cand;
                        }
                    }
                }
                if (ymap == null) continue; // папка без ymap — не спот
                var spot = new Spot
                {
                    Id = id, Name = name, Description = desc, Dir = dir,
                    YmapFile = ymap,
                    YmapEntry = Path.GetFileName(ymap).ToLowerInvariant(),
                };
                foreach (var img in Directory.GetFiles(dir, "preview*")
                    .Concat(Directory.GetFiles(dir, "*.jpg"))
                    .Concat(Directory.GetFiles(dir, "*.png")).Distinct())
                    if (!spot.Images.Contains(img)) spot.Images.Add(img);
                spot.Preview = spot.Images.FirstOrDefault() ?? "";
                list.Add(spot);
            }
            catch { }
        }
        return list;
    }

    private static void CreateReadme(string root)
    {
        var dir = Path.Combine(root, "_example");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spot.json"), """
        {
          "id": "port",
          "name": "Port (пример)",
          "description": "Положи рядом чистый .ymap этого спота и preview.jpg. Имя файла = имя записи в ymap.rpf."
        }
        """);
        File.WriteAllText(Path.Combine(dir, "КАК_ДОБАВИТЬ.txt"),
            "1. Скопируй чистый .ymap спота в эту папку (или создай свою папку в spots/).\r\n2. Рядом положи preview.jpg.\r\n3. Перезапусти приложение — спот появится с галочкой.");
    }
}
