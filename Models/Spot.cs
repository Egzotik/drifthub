using System.ComponentModel;

namespace DriftHub.Models;

// Spot = one clean .ymap injected into the nested ymap.rpf.
// Folder layout: spots/<id>/spot.json + <id>.ymap (or any *.ymap) + preview.jpg/png
public sealed class Spot : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Dir { get; set; } = "";
    public string YmapFile { get; set; } = "";
    public string YmapEntry { get; set; } = "";
    public List<string> Images { get; set; } = new();
    // Первый preview для карточки (пусто = показать заглушку).
    public string Preview { get; set; } = "";

    private bool _installed;
    public bool IsInstalled
    {
        get => _installed;
        set { if (_installed != value) { _installed = value; PropertyChanged?.Invoke(this, new(nameof(IsInstalled))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
