using CommunityToolkit.Mvvm.ComponentModel;

namespace VoiceFlow.Desktop.ViewModels;

public enum Page { Home, Meetings, Insights, Dictionary, Snippets, Style, Scratchpad, Settings, Help }

/// <summary>One sidebar entry. Selection is a view-model property so arrow keys, clicks and code agree.</summary>
public sealed partial class NavItem : ObservableObject
{
    readonly Action<NavItem> _select;

    public NavItem(Page page, string title, string icon, Action<NavItem> select)
    {
        Page = page;
        Title = title;
        IconKey = icon;
        _select = select;
    }

    public Page Page { get; }
    public string Title { get; }
    public string IconKey { get; }

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _select(this);
    }
}
