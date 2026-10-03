using System.IO;
using Microsoft.Win32;

namespace DriftHub.Services;

/// <summary>
/// Поиск и проверка папки GTA V. Пока только проверка GTA5.exe,
/// без записи в RPF — это сделает фаза 2 через CodeWalker.Core.
/// </summary>
public static class GtaPathService
{
    private static readonly string[] CommonPaths =
    {
        @"C:\Program Files (x86)\Steam\SteamApps\common\Grand Theft Auto V",
        @"C:\Program Files\Epic Games\GTAV",
        @"C:\Program Files\Rockstar Games\Grand Theft Auto V",
        @"D:\SteamLibrary\steamapps\common\Grand Theft Auto V",
    };

    public static string? AutoDetect()
    {
        foreach (var p in CommonPaths)
            if (IsValid(p)) return p;
        return null;
    }

    public static bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return File.Exists(Path.Combine(path, "GTA5.exe"));
    }

    public static string? AskUserViaExePicker()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "GTA5.exe|GTA5.exe",
            FileName = "GTA5.exe",
            Title = "Укажи GTA5.exe (папка с игрой)"
        };
        if (dlg.ShowDialog() == true)
            return Path.GetDirectoryName(dlg.FileName);
        return null;
    }
}
