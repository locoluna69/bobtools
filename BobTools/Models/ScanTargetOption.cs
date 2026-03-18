using CommunityToolkit.Mvvm.ComponentModel;

namespace BobTools.Models;

public sealed partial class ScanTargetOption : ObservableObject
{
    public ScanTargetOption(string resource, bool isSelected = false)
    {
        Resource = resource;
        this.isSelected = isSelected;
    }

    public string Resource { get; }

    [ObservableProperty] private bool isSelected;
}