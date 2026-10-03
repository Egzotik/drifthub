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

        var list = new List<Spot>();
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
        {
            // Служебная папка-шаблон рядом с exe — не спот, пропускаем.
            if (string.Equals(Path.GetFileName(dir), "_example", StringComparison.OrdinalIgnoreCase))
                continue;
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
}
