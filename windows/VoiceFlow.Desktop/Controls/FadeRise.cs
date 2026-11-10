using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace VoiceFlow.Desktop.Controls;

/// <summary>Page transition: the new page fades in and rises 8 px in 160 ms.</summary>
public sealed class FadeRise : IPageTransition
{
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (from != null) from.IsVisible = false;
        if (to == null) return;
        to.IsVisible = true;
        var rise = new TranslateTransform();
        to.RenderTransform = rise;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0.0), new Setter(TranslateTransform.YProperty, 8.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0), new Setter(TranslateTransform.YProperty, 0.0) } },
            },
        };
        await animation.RunAsync(to, cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() => { to.RenderTransform = null; to.Opacity = 1; });
    }
}
