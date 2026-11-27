using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace InstantReplay;

// WinUI port of draw_orb from the supplied media_indicator.lua (@author cracker).
public sealed class MediaOrb : Canvas
{
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private const int SpokeCount = 16;
    private const int TrailCount = 15;
    private const double CycleSeconds = 24;
    private const double OrbitRadius = 8.8;
    private readonly Stopwatch _clock = new();
    private readonly Spoke[] _spokes = new Spoke[SpokeCount];
    private readonly Ellipse[] _trail = new Ellipse[TrailCount];
    private readonly Canvas _ring = new();
    private readonly RotateTransform _ringRotation = new();
    private readonly Ellipse _halo = CreateDisc();
    private readonly Ellipse _coreHalo = CreateDisc();
    private readonly Ellipse _coreDisc = CreateDisc();
    private readonly Ellipse _core = CreateDisc();
    private readonly Ellipse _satelliteHalo = CreateDisc();
    private readonly SolidColorBrush _accent = new();
    private readonly SolidColorBrush _light = new();
    private TimeSpan? _previousFrame;
    private Color _ink;
    private Color _highlight;

    public MediaOrb()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);

        Children.Add(_halo);
        _ring.RenderTransform = _ringRotation;
        for (int i = 0; i < 28; i++)
        {
            var dot = CreateDisc();
            dot.Fill = new SolidColorBrush(White);
            dot.Opacity = 0.20;
            _ring.Children.Add(dot);
        }
        Children.Add(_ring);

        for (int i = 0; i < _spokes.Length; i++)
        {
            var spoke = new Spoke();
            _spokes[i] = spoke;
            Children.Add(spoke.Bar);
            Children.Add(spoke.Tip);
        }

        _coreDisc.Fill = _accent;
        _coreDisc.Opacity = 0.32;
        _core.Fill = new SolidColorBrush(White);
        Children.Add(_coreDisc);
        Children.Add(_coreHalo);
        Children.Add(_core);
        Children.Add(_satelliteHalo);

        for (int i = TrailCount - 1; i >= 0; i--)
        {
            double strength = 1 - i / (double)TrailCount;
            _trail[i] = CreateDisc();
            _trail[i].Fill = i == 0 ? new SolidColorBrush(White) : _light;
            _trail[i].Opacity = i == 0 ? 0.96 : 0.69 * strength * strength;
            Children.Add(_trail[i]);
        }

        ActualThemeChanged += (_, _) =>
        {
            ApplyTheme();
            Draw(_clock.Elapsed.TotalSeconds);
        };
        SizeChanged += (_, _) =>
        {
            ArrangeOrbit();
            Draw(_clock.Elapsed.TotalSeconds);
        };
        Unloaded += (_, _) => SetPlaying(false);
        ApplyTheme();
    }

    public void SetPlaying(bool play)
    {
        if (play == _clock.IsRunning) return;
        if (play)
        {
            _clock.Start();
            _previousFrame = null;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            _clock.Stop();
        }
    }

    private void OnRendering(object? sender, object args)
    {
        if (args is RenderingEventArgs frame)
        {
            if (_previousFrame == frame.RenderingTime) return;
            _previousFrame = frame.RenderingTime;
        }
        Draw(_clock.Elapsed.TotalSeconds);
    }

    private void ApplyTheme()
    {
        _ink = ActualTheme == ElementTheme.Light ? Color.FromArgb(255, 28, 113, 174) : Color.FromArgb(255, 65, 158, 233);
        var ice = Color.FromArgb(255, 163, 231, 246);
        _highlight = Mix(ice, White, 0.24);
        _accent.Color = _ink;
        foreach (var spoke in _spokes) spoke.Ink.Color = _ink;
        _light.Color = _highlight;
        _halo.Fill = Glow(_ink);
        _coreHalo.Fill = Glow(_highlight);
        _satelliteHalo.Fill = Glow(_ink);
    }

    private void Draw(double now)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        double scale = size / 23;
        double cx = ActualWidth * 0.5;
        double cy = ActualHeight * 0.5;
        double orbit = OrbitRadius * scale;
        // Integer phase multiples make the complete pattern repeat without a seam.
        double phase = (now % CycleSeconds) / CycleSeconds * Math.Tau;
        double breath = 0.5 - 0.5 * Math.Cos(6 * phase);
        double rotation = phase;
        double satellite = 3 * phase;
        double coreRadius = (2.85 + 0.42 * breath) * scale;
        Place(_halo, cx, cy, (8.2 + 1.3 * breath) * scale);
        _halo.Opacity = 0.18 + 0.13 * breath;
        _ringRotation.Angle = -rotation * 180 / Math.PI;

        for (int i = 0; i < _spokes.Length; i++)
        {
            var spoke = _spokes[i];
            double radial = i * Math.Tau / SpokeCount;
            double angle = rotation + radial;
            double crest = 0.5 + 0.5 * Math.Cos(3 * radial - 4 * phase);
            double echo = 0.5 + 0.5 * Math.Cos(5 * radial + 3 * phase);
            double level = 0.16 + 0.52 * crest + 0.16 * echo + 0.14 * breath;
            double length = (1.0 + 3.65 * level) * scale;
            double x = Math.Cos(angle);
            double y = Math.Sin(angle);
            var transform = (CompositeTransform)spoke.Bar.RenderTransform;
            transform.TranslateX = cx + x * coreRadius;
            transform.TranslateY = cy + y * coreRadius;
            transform.ScaleX = length;
            transform.ScaleY = (0.66 + 0.34 * level) * scale;
            transform.Rotation = angle * 180 / Math.PI;
            spoke.TipInk.Color = Mix(_ink, _highlight, 0.28 + 0.72 * level);
            spoke.Bar.Opacity = spoke.Tip.Opacity = 0.43 + 0.52 * level;
            Place(spoke.Tip, cx + x * (coreRadius + length), cy + y * (coreRadius + length), (0.37 + 0.21 * level) * scale);
        }

        Place(_coreDisc, cx, cy, coreRadius - 0.8 * scale);
        Place(_coreHalo, cx, cy, (2.45 + 0.85 * breath) * scale);
        _coreHalo.Opacity = 0.38 + 0.17 * breath;
        Place(_core, cx, cy, (1.05 + 0.38 * breath) * scale);

        for (int j = TrailCount - 1; j >= 0; j--)
        {
            double angle = -satellite + j * 0.075;
            double x = cx + Math.Cos(angle) * orbit;
            double y = cy + Math.Sin(angle) * orbit;
            Place(_trail[j], x, y, (1.15 - 0.91 * j / TrailCount) * scale);
            if (j != 0) continue;
            Place(_satelliteHalo, x, y, 2.9 * scale);
            _satelliteHalo.Opacity = 0.43 + 0.14 * breath;
        }
    }

    private void ArrangeOrbit()
    {
        double cx = ActualWidth * 0.5;
        double cy = ActualHeight * 0.5;
        double scale = Math.Min(ActualWidth, ActualHeight) / 23;
        _ringRotation.CenterX = cx;
        _ringRotation.CenterY = cy;
        for (int j = 0; j < _ring.Children.Count; j++)
        {
            double angle = j * Math.Tau / _ring.Children.Count;
            Place((Ellipse)_ring.Children[j], cx + Math.Cos(angle) * OrbitRadius * scale,
                cy + Math.Sin(angle) * OrbitRadius * scale, 0.42 * scale);
        }
    }

    private static Ellipse CreateDisc() => new()
    {
        Width = 2, Height = 2,
        RenderTransform = new CompositeTransform { CenterX = 1, CenterY = 1 },
    };

    private static void Place(Ellipse element, double x, double y, double radius)
    {
        var transform = (CompositeTransform)element.RenderTransform;
        transform.ScaleX = transform.ScaleY = radius;
        transform.TranslateX = x - 1;
        transform.TranslateY = y - 1;
    }

    private static Color Mix(Color from, Color to, double amount) =>
        Color.FromArgb(255,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static RadialGradientBrush Glow(Color color)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        foreach (var (offset, alpha) in new (double, byte)[] { (0, 200), (0.25, 150), (0.5, 90), (0.75, 42), (1, 0) })
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = Color.FromArgb(alpha, color.R, color.G, color.B) });
        return brush;
    }

    private sealed class Spoke
    {
        public SolidColorBrush Ink { get; } = new();
        public SolidColorBrush TipInk { get; } = new();
        public Rectangle Bar { get; }
        public Ellipse Tip { get; }

        public Spoke()
        {
            Bar = new Rectangle
            {
                Width = 1, Height = 1, Fill = Ink,
                RenderTransform = new CompositeTransform { CenterY = 0.5 },
            };
            SetTop(Bar, -0.5);
            Tip = CreateDisc();
            Tip.Fill = TipInk;
        }
    }
}
