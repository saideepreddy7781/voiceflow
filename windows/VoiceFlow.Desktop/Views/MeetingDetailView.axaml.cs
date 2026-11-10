using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
using VoiceFlow.Desktop.ViewModels;

namespace VoiceFlow.Desktop.Views;

public partial class MeetingDetailView : UserControl
{
    public MeetingDetailView() => AvaloniaXamlLoader.Load(this);

    void OnTitleLost(object? sender, RoutedEventArgs e) => (DataContext as MeetingDetailViewModel)?.CommitTitleCommand.Execute(null);

    void OnTitleKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) (DataContext as MeetingDetailViewModel)?.CommitTitleCommand.Execute(null);
    }
}
