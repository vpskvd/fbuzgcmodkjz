using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.UI;

namespace InstantReplay;

public sealed class Guilloche : Grid
{
    private const double Center = 29;
    private const int Samples = 180;
    private readonly Canvas _outer;
    private readonly Canvas _inner;
    private bool _playing;

    public Guilloche()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        // Two line engravings sit on separate composition layers. The paths are built once.
        _outer = BuildLayer(radius: 16.5, amplitude: 6.2, lobes: 3, phase: 0.3);
        _inner = BuildLayer(radius: 13, amplitude: 6.5, lobes: 4, phase: -0.4);
        Children.Add(_outer);
        Children.Add(_inner);
        ActualThemeChanged += (_, _) => ApplyTheme();
        Unloaded += (_, _) => SetPlaying(false);
        ApplyTheme();
    }

    private static Canvas BuildLayer(double radius, double amplitude, int lobes, double phase)
    {
        var layer = new Canvas { Width = Center * 2, Height = Center * 2 };
        for (int line = 0; line < 4; line++)
        {
            var figure = new PathFigure { IsClosed = true, IsFilled = false };
            var segment = new PolyLineSegment();
            for (int step = 0; step < Samples; step++)
            {
                double angle = Math.Tau * step / Samples;
                double wave = lobes * angle + phase + line * 0.26;
                double length = radius + line * 0.48 + amplitude * Math.Cos(wave) + 1.1 * Math.Sin(2 * wave);
                Point point = new(Center + length * Math.Cos(angle), Center + length * Math.Sin(angle));
                if (step == 0) figure.StartPoint = point;
                else segment.Points.Add(point);
            }
            figure.Segments.Add(segment);
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            layer.Children.Add(new Path { Data = geometry, StrokeThickness = line == 3 ? 1.10 : 0.68 });
        }
        return layer;
    }

    private void ApplyTheme()
    {
        bool light = ActualTheme == ElementTheme.Light;
        ApplyInk(_outer, light ? Color.FromArgb(255, 122, 81, 43) : Color.FromArgb(255, 236, 184, 122));
        ApplyInk(_inner, light ? Color.FromArgb(255, 56, 74, 101) : Color.FromArgb(255, 182, 202, 232));
    }

    private static void ApplyInk(Canvas layer, Color ink)
    {
        for (int i = 0; i < layer.Children.Count; i++)
        {
            var path = (Path)layer.Children[i];
            path.Stroke = new SolidColorBrush(ink);
            path.Opacity = i switch { 0 => 0.38, 1 => 0.57, 2 => 0.76, _ => 1 };
        }
    }

    public void SetPlaying(bool play)
    {
        if (_playing == play) return;
        _playing = play;

        Animate(_outer, 120, 14, play);
        Animate(_inner, -90, 18, play);
    }

    private static void Animate(Canvas layer, float angle, double seconds, bool play)
    {
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        visual.RotationAngleInDegrees = 0;
        if (!play) return;

        visual.CenterPoint = new Vector3((float)Center, (float)Center, 0);
        var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.InsertKeyFrame(0, 0);
        rotation.InsertKeyFrame(1, angle, visual.Compositor.CreateLinearEasingFunction());
        rotation.Duration = TimeSpan.FromSeconds(seconds);
        rotation.IterationBehavior = AnimationIterationBehavior.Forever;
        // Each layer has rotational symmetry, so its last pose matches its first pose.
        visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), rotation);
    }
}
