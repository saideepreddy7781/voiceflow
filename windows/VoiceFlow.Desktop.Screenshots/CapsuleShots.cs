using Avalonia;
using Avalonia.Controls;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VoiceFlow.Core.FlowBar;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Desktop.Services.Demo;
using VoiceFlow.Desktop.ViewModels;
using VoiceFlow.Desktop.Views;

/// <summary>
/// The real Flow bar window rendered on its own (so only what Avalonia itself draws is in the picture), then laid
/// over a busy desktop where the Windows host would put it. The "-hits" pictures outline the parts that take clicks;
/// everything else passes through to the desktop.
/// </summary>
static class CapsuleShots
{
    const double Width = 1000, Height = 620, Taskbar = 44;
    static readonly PxRect Work = new(0, 0, (int)Width, (int)(Height - Taskbar));
    static readonly float[] Bars = [0.2f, 0.4f, 0.75f, 0.55f, 0.95f, 0.7f, 0.5f, 0.85f, 0.45f, 0.25f, 0.15f];

    public static void All(string dir, DateTimeOffset clock)
    {
        var desktopWindow = BusyDesktop();
        var desktop = Copy(Render(desktopWindow));
        desktopWindow.Close();
        var toast = new FlowToast("Pasted into Notepad", "Copied to the clipboard too.", "Undo", () => { });
        var states = new (string Name, Action<DemoDictation> Set, PxPoint? Dropped, bool Menu, bool Hits)[]
        {
            ("listening", d => d.Set(FlowPhase.Listening, Bars), null, false, false),
            ("hands-free", d => d.Set(FlowPhase.HandsFree, Bars), null, false, true),
            ("command", d => d.Set(FlowPhase.Command, Bars), null, false, false),
            ("processing", d => d.Set(FlowPhase.Processing, Bars), null, false, false),
            ("toast-while-listening", d => d.Set(FlowPhase.Listening, Bars, toast), null, false, true),
            ("toast-alone", d => d.Set(FlowPhase.Idle, null, new FlowToast("Nothing was heard", "Check the microphone in Settings.")), null, false, false),
            ("meeting-chip", d => d.Set(FlowPhase.Idle, null, null, DateTimeOffset.Now.AddMinutes(-12)), null, false, false),
            ("resting", d => d.ShowWhenIdle = true, null, false, false),
            ("menu", d => d.Set(FlowPhase.HandsFree, Bars), null, true, true),
            ("dragged-top-left-toast", d => d.Set(FlowPhase.HandsFree, Bars, toast), new PxPoint(40, 20), false, true),
            ("dragged-right-edge-toast", d => d.Set(FlowPhase.Listening, Bars, toast), new PxPoint(990, 300), false, true),
            ("dragged-middle", d => d.Set(FlowPhase.HandsFree, Bars), new PxPoint(620, 260), false, false),
        };
        foreach (var (name, set, dropped, menu, hits) in states)
        {
            var dictation = new DemoDictation();
            set(dictation);
            var services = new DemoServices(clock, animated: false);
            var vm = new FlowBarViewModel(new DemoServicesView(services, dictation), runClock: false) { ShowMenu = menu };
            var bar = new FlowBarWindow { DataContext = vm };
            bar.Show();
            Pump(bar);
            var (position, parts) = Place(bar, dropped);
            var image = RenderWithAlpha(bar);
            Save(Compose(desktop, image, position, parts, outline: false), Path.Combine(dir, $"capsule-{name}.png"));
            if (hits) Save(Compose(desktop, image, position, parts, outline: true), Path.Combine(dir, $"capsule-{name}-hits.png"));
            bar.Close();
        }
    }

    /// <summary>What FlowBarHost does, at 100% on a desktop the size of the picture.</summary>
    static (Point Position, IReadOnlyList<RoundedBox> Parts) Place(FlowBarWindow bar, PxPoint? dropped)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            var anchor = bar.AnchorRect() ?? new Rect(bar.ClientSize);
            var size = BarPlacement.ToPixels(anchor.Width, anchor.Height, 1);
            var centre = dropped is { } d ? BarPlacement.Snap(d, size, Work, 1).Centre : BarPlacement.Home(Work, size, 1);
            var layout = bar.BarLayout!;
            if (layout.CapsuleSize.Width > 0 && layout.ExtrasSize.Width > 0)
            {
                var (below, offset) = BarPlacement.Extras(centre, BarPlacement.ToPixels(layout.CapsuleSize.Width, layout.CapsuleSize.Height, 1),
                    BarPlacement.ToPixels(layout.ExtrasSize.Width, layout.ExtrasSize.Height, 1), (int)layout.Spacing, Work, 1);
                bar.SetExtras(below, offset);
            }
            Pump(bar);
            anchor = bar.AnchorRect() ?? new Rect(bar.ClientSize);
            if (pass == 2) return (new Point(centre.X - anchor.Center.X, centre.Y - anchor.Center.Y), bar.ShapeParts());
        }
        throw new InvalidOperationException("unreachable");
    }

    static void Pump(Window window)
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(8);
            Dispatcher.UIThread.RunJobs();
        }
    }

    static Bitmap Render(Window window)
    {
        Pump(window);
        window.SetRenderScaling(2);
        Pump(window);
        return window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
    }

    /// <summary>
    /// The window's whole visual tree, its own background included, drawn into a bitmap that keeps alpha. The
    /// headless window capture is opaque, so it can't show what the bar leaves transparent.
    /// </summary>
    static Bitmap RenderWithAlpha(Window window)
    {
        Pump(window);
        var size = window.ClientSize;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2)), new Vector(192, 192));
        bitmap.Render(window);
        return Copy(bitmap);
    }

    /// <summary>A copy that no longer depends on the window it was captured from.</summary>
    static Bitmap Copy(Bitmap frame)
    {
        var stream = new MemoryStream();
#pragma warning disable CS0618
        frame.Save(stream);
#pragma warning restore CS0618
        stream.Position = 0;
        return new Bitmap(stream);
    }

    static Window Compose(Bitmap desktop, Bitmap bar, Point position, IReadOnlyList<RoundedBox> parts, bool outline)
    {
        var canvas = new Canvas { Width = Width, Height = Height };
        canvas.Children.Add(new Image { Source = desktop, Width = Width, Height = Height });
        var barImage = new Image { Source = bar, Width = bar.PixelSize.Width / 2.0, Height = bar.PixelSize.Height / 2.0 };
        Canvas.SetLeft(barImage, position.X);
        Canvas.SetTop(barImage, position.Y);
        canvas.Children.Add(barImage);
        if (outline)
        {
            // The window's rectangle, dashed: everything inside it but outside the red outlines is click-through.
            var frame = new Rectangle
            {
                Width = barImage.Width, Height = barImage.Height, Stroke = Brushes.White, StrokeThickness = 1,
                StrokeDashArray = [4, 3], Opacity = 0.9,
            };
            Canvas.SetLeft(frame, position.X);
            Canvas.SetTop(frame, position.Y);
            canvas.Children.Add(frame);
            foreach (var p in parts)
            {
                var box = new Border
                {
                    Width = p.Width, Height = p.Height, CornerRadius = new CornerRadius(p.Radius),
                    BorderBrush = new SolidColorBrush(Color.Parse("#FF3B30")), BorderThickness = new Thickness(1.5),
                    Background = new SolidColorBrush(Color.Parse("#33FF3B30")),
                };
                Canvas.SetLeft(box, position.X + p.X);
                Canvas.SetTop(box, position.Y + p.Y);
                canvas.Children.Add(box);
            }
            var legend = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#CC000000")), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
                Child = new TextBlock
                {
                    Text = "Red: takes clicks. Dashed: the window. Everything else reaches the app underneath.",
                    Foreground = Brushes.White, FontSize = 12,
                },
            };
            Canvas.SetRight(legend, 12);
            Canvas.SetTop(legend, 12);
            canvas.Children.Add(legend);
        }
        var window = new Window { Width = Width, Height = Height, Content = canvas };
        window.Show();
        return window;
    }

    static void Save(Window window, string path)
    {
        var frame = Render(window);
        using (var stream = File.Create(path))
        {
#pragma warning disable CS0618 // The replacement overload needs an encoder options type this tool does not use.
            frame.Save(stream);
#pragma warning restore CS0618
        }
        window.Close();
        Console.WriteLine(path);
    }

    /// <summary>A desktop with an editor, a photo-like grid of colour, small text and a taskbar: hard to look clean on.</summary>
    static Window BusyDesktop()
    {
        var root = new Canvas { Width = Width, Height = Height, Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#0F3057"), 0), new GradientStop(Color.Parse("#00587A"), 0.5), new GradientStop(Color.Parse("#E7A977"), 1) },
        } };
        var palette = new[] { "#F94144", "#F3722C", "#F8961E", "#F9C74F", "#90BE6D", "#43AA8B", "#577590", "#FFFFFF", "#222222" };
        var random = new Random(7);
        for (int i = 0; i < 160; i++)
        {
            var size = 10 + random.Next(60);
            var tile = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(random.Next(2) == 0 ? size / 2.0 : 3),
                Background = new SolidColorBrush(Color.Parse(palette[random.Next(palette.Length)]), 0.35 + random.NextDouble() * 0.6),
            };
            Canvas.SetLeft(tile, random.Next((int)Width));
            Canvas.SetTop(tile, random.Next((int)(Height - Taskbar)));
            root.Children.Add(tile);
        }
        // An editor window across the middle and bottom, where the bar usually sits.
        var editor = new Border
        {
            Width = 640, Height = 420, Background = new SolidColorBrush(Color.Parse("#F5F5F0")), CornerRadius = new CornerRadius(8),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #66000000"), Padding = new Thickness(18, 14),
        };
        var lines = new StackPanel { Spacing = 3 };
        lines.Children.Add(new TextBlock { Text = "Untitled - Notepad", FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8) });
        var words = "the quick brown fox jumps over the lazy dog while the meeting notes keep scrolling past every line of this busy page".Split(' ');
        for (int i = 0; i < 21; i++)
        {
            var text = string.Join(' ', Enumerable.Range(0, 9 + random.Next(6)).Select(_ => words[random.Next(words.Length)]));
            lines.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(i % 5 == 0 ? "#B00020" : "#202020")) });
        }
        editor.Child = lines;
        Canvas.SetLeft(editor, 180);
        Canvas.SetTop(editor, 150);
        root.Children.Add(editor);
        var taskbar = new Border { Width = Width, Height = Taskbar, Background = new SolidColorBrush(Color.Parse("#E6202020")) };
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new[] { "#0078D4", "#F25022", "#7FBA00", "#FFB900", "#737373" })
            icons.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse(c)) });
        taskbar.Child = new Grid { Width = Width, Children = { icons } };
        Canvas.SetTop(taskbar, Height - Taskbar);
        root.Children.Add(taskbar);
        var window = new Window { Width = Width, Height = Height, Content = root };
        window.Show();
        return window;
    }
}
