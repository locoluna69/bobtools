using CommunityToolkit.Mvvm.ComponentModel;

namespace BobTools.Models;

public sealed partial class ReviewFilterOption : ObservableObject
{
    public ReviewFilterOption(string key, string displayName, bool isSelected = true)
    {
        Key = key;
        DisplayName = displayName;
        this.isSelected = isSelected;
    }

    public string Key { get; }

    public string DisplayName { get; }

    [ObservableProperty] private bool isSelected;
}