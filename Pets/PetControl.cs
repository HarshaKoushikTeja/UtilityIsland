using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicIsland.Pets;

public enum PetKind
{
    Cat,
    Dog,
    Fox,
    Bunny,
    Slime
}

/// <summary>
/// A small vector pet. It walks, sits, blinks, bounces to music, naps when the
/// PC is idle, and can be sent after something (food, a ball) with SeekX.
/// Drawn from shapes so there are no image assets to ship.
/// </summary>
public sealed class PetControl : Canvas
{
    public const double PetWidth = 26;
    public const double PetHeight = 24;

    private static readonly Brush HeartBrush = Fill("#FFFF6482");

    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _flip = new(1, 1, PetWidth / 2, PetHeight / 2);
    private readonly TranslateTransform _bob = new();
    private readonly TranslateTransform _legA = new();
    private readonly TranslateTransform _legB = new();
    private readonly ScaleTransform _eyes = new(1, 1);
    private TextBlock? _zzz;

    private PetKind _kind = PetKind.Cat;
    private double _x;
    private double _direction = 1;
    private double _time;
    private bool _walking = true;
    private double _nextSwitch = 3;
    private double _nextBlink = 2;
    private double _blinkLeft;
    private double _hopLeft;
    private double _chompLeft;

    /// <summary>When false the pet stays put (used for the previews in Settings).</summary>
    public bool Roam { get; set; } = true;

    /// <summary>Eyes closed, floating "z". Set when the PC has been idle for a while.</summary>
    public bool Sleeping { get; set; }

    /// <summary>Bounces in place. Set while music is playing.</summary>
    public bool Dancing { get; set; }

    /// <summary>Freezes all animation (island hidden behind a fullscreen app).</summary>
    public bool Paused { get; set; }

    /// <summary>Walking speed multiplier; a hungry pet drags its feet.</summary>
    public double SpeedFactor { get; set; } = 1;

    /// <summary>When set, the pet runs toward this x position (in its host's coordinates).</summary>
    public double? SeekX { get; set; }

    /// <summary>Raised when the pet itself is clicked.</summary>
    public event Action? Clicked;

    public double Scale
    {
        get => _scale.ScaleX;
        set
        {
            _scale.ScaleX = value;
            _scale.ScaleY = value;
        }
    }

    /// <summary>Horizontal centre of the pet in its host's coordinates.</summary>
    public double CenterX => _x + PetWidth * Scale / 2;

    public PetKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Build();
        }
    }

    public PetControl()
    {
        Width = PetWidth;
        Height = PetHeight;
        Cursor = Cursors.Hand;

        var transform = new TransformGroup();
        transform.Children.Add(_scale);
        transform.Children.Add(_move);
        RenderTransform = transform;

        Build();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick(0.05);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
    }

    public static PetKind Parse(string? name)
    {
        return Enum.TryParse(name, true, out PetKind kind) ? kind : PetKind.Cat;
    }

    // ---- reactions -----------------------------------------------------

    /// <summary>A heart floats up and the pet does a happy hop.</summary>
    public void ShowLove()
    {
        var heart = new TextBlock
        {
            Text = "♥",
            FontSize = 9,
            Foreground = HeartBrush,
            IsHitTestVisible = false
        };
        SetLeft(heart, PetWidth / 2 - 4 + (_random.NextDouble() * 8 - 4));
        SetTop(heart, 4);
        Children.Add(heart);

        var duration = TimeSpan.FromMilliseconds(900);
        var rise = new DoubleAnimation(4, -12, duration);
        var fade = new DoubleAnimation(1, 0, duration);
        fade.Completed += (_, _) => Children.Remove(heart);
        heart.BeginAnimation(TopProperty, rise);
        heart.BeginAnimation(OpacityProperty, fade);

        _hopLeft = 0.6;
    }

    /// <summary>Quick nibbling motion for the given number of seconds.</summary>
    public void Chomp(double seconds)
    {
        _chompLeft = seconds;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Clicked != null)
        {
            Clicked.Invoke();
            e.Handled = true; // a click on the pet pets it; it shouldn't also open the island
        }
    }

    // ---- animation -----------------------------------------------------

    private void Tick(double dt)
    {
        if (Paused) return;

        _time += dt;
        if (_hopLeft > 0) _hopLeft -= dt;
        if (_chompLeft > 0) _chompLeft -= dt;

        bool seeking = SeekX != null;
        bool reacting = _hopLeft > 0 || _chompLeft > 0;

        if (Sleeping && !seeking && !reacting)
        {
            _eyes.ScaleY = 0.15;
            _legA.Y = 0;
            _legB.Y = 0;
            _bob.Y = Math.Sin(_time * 1.5) * 0.6;
            if (_zzz != null)
            {
                _zzz.Visibility = Visibility.Visible;
                _zzz.Opacity = 0.45 + 0.4 * Math.Sin(_time * 2);
            }
            _move.X = _x;
            return;
        }

        if (_zzz != null) _zzz.Visibility = Visibility.Collapsed;

        // Blink every few seconds.
        _nextBlink -= dt;
        if (_nextBlink <= 0)
        {
            _blinkLeft = 0.15;
            _nextBlink = 2 + _random.NextDouble() * 4;
        }
        if (_blinkLeft > 0)
        {
            _blinkLeft -= dt;
            _eyes.ScaleY = 0.15;
        }
        else
        {
            _eyes.ScaleY = 1;
        }

        // Decide whether to walk this tick.
        if (seeking)
        {
            double gap = SeekX!.Value - CenterX;
            if (Math.Abs(gap) > 3)
            {
                _direction = gap > 0 ? 1 : -1;
                _walking = true;
            }
            else
            {
                _walking = false;
            }
        }
        else if (Roam && !Dancing && !reacting)
        {
            _nextSwitch -= dt;
            if (_nextSwitch <= 0)
            {
                _walking = !_walking;
                _nextSwitch = 2 + _random.NextDouble() * 5;
                if (_walking && _random.NextDouble() < 0.5) _direction = -_direction;
            }
        }
        else
        {
            _walking = false;
        }

        double hostWidth = (Parent as FrameworkElement)?.ActualWidth ?? PetWidth;
        double maxX = Math.Max(0, hostWidth - PetWidth * Scale);

        if (_walking)
        {
            double speed = (seeking ? 60 : 14) * SpeedFactor;
            _x += _direction * speed * dt;
            if (_x >= maxX)
            {
                _x = maxX;
                if (!seeking) _direction = -1;
            }
            if (_x <= 0)
            {
                _x = 0;
                if (!seeking) _direction = 1;
            }

            double step = Math.Sin(_time * (seeking ? 24 : 14));
            _legA.Y = -Math.Max(0, step) * 1.5;
            _legB.Y = -Math.Max(0, -step) * 1.5;
            _bob.Y = step * 0.4;
        }
        else
        {
            _x = Math.Clamp(_x, 0, maxX);
            _legA.Y = 0;
            _legB.Y = 0;

            if (_chompLeft > 0) _bob.Y = Math.Sin(_time * 30) * 1.2;
            else if (_hopLeft > 0) _bob.Y = -Math.Abs(Math.Sin(_time * 10)) * 3.5;
            else if (Dancing) _bob.Y = -Math.Abs(Math.Sin(_time * 7)) * 2.2;
            else _bob.Y = Math.Sin(_time * 2) * 0.4;
        }

        _flip.ScaleX = _direction;
        _move.X = _x;
    }

    // ---- drawing -------------------------------------------------------

    private void Build()
    {
        Children.Clear();

        // Invisible pad so the whole pet is easy to click, not just its pixels.
        Children.Add(Box(0, 0, PetWidth, PetHeight, Brushes.Transparent, 0));

        // Everything that turns around with the pet goes in "body".
        var body = new Canvas { Width = PetWidth, Height = PetHeight, RenderTransform = _flip };
        // The part that bobs up and down (everything except the legs).
        var upper = new Canvas { Width = PetWidth, Height = PetHeight, RenderTransform = _bob };

        switch (_kind)
        {
            case PetKind.Cat:
                BuildCat(body, upper);
                break;
            case PetKind.Dog:
                BuildDog(body, upper);
                break;
            case PetKind.Fox:
                BuildFox(body, upper);
                break;
            case PetKind.Bunny:
                BuildBunny(body, upper);
                break;
            default:
                BuildSlime(upper);
                break;
        }

        body.Children.Add(upper);
        Children.Add(body);

        _zzz = new TextBlock
        {
            Text = "z",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        SetLeft(_zzz, PetWidth - 4);
        SetTop(_zzz, -3);
        Children.Add(_zzz);
    }

    private void BuildCat(Canvas body, Canvas upper)
    {
        Brush fur = Fill("#F4A259");
        Brush dark = Fill("#C97B3A");
        AddLegs(body, fur);
        upper.Children.Add(Box(1, 8, 3, 10, dark));                        // tail
        upper.Children.Add(Oval(3, 11, 16, 10, fur));                      // body
        upper.Children.Add(Tri(fur, 14, 6.5, 16, 0.5, 19, 5));             // ears
        upper.Children.Add(Tri(fur, 20, 5, 23, 0.5, 24.5, 6.5));
        upper.Children.Add(Oval(13, 4, 12, 11, fur));                      // head
        AddEyes(upper, 17, 21.5, 8);
    }

    private void BuildDog(Canvas body, Canvas upper)
    {
        Brush fur = Fill("#C68B59");
        Brush dark = Fill("#8A5A34");
        AddLegs(body, fur);
        upper.Children.Add(Box(1, 7, 3, 8, fur));                          // tail
        upper.Children.Add(Oval(3, 11, 16, 10, fur));                      // body
        upper.Children.Add(Oval(13, 4, 12, 11, fur));                      // head
        upper.Children.Add(Oval(12.5, 5, 4, 8, dark));                     // floppy ears
        upper.Children.Add(Oval(21.5, 5, 4, 8, dark));
        upper.Children.Add(Oval(19.5, 10, 5.5, 4, Fill("#EBD3BC")));       // snout
        upper.Children.Add(Oval(23, 10.5, 2, 1.6, Fill("#2B2B2B")));       // nose
        AddEyes(upper, 17.5, 21, 7.5);
    }

    private void BuildFox(Canvas body, Canvas upper)
    {
        Brush fur = Fill("#F2762E");
        Brush white = Fill("#FFF4E8");
        Brush dark = Fill("#5A2D0C");
        AddLegs(body, dark);
        upper.Children.Add(Oval(-2, 9, 9, 6, fur));                        // bushy tail
        upper.Children.Add(Oval(-2, 10.5, 4, 3.5, white));                 // tail tip
        upper.Children.Add(Oval(3, 11, 16, 10, fur));                      // body
        upper.Children.Add(Tri(dark, 14, 6.5, 15.5, 0, 19, 5));            // ears
        upper.Children.Add(Tri(dark, 20, 5, 23.5, 0, 24.5, 6.5));
        upper.Children.Add(Oval(13, 4, 12, 11, fur));                      // head
        upper.Children.Add(Oval(17, 10.5, 8, 4.5, white));                 // cheeks
        AddEyes(upper, 17, 21.5, 7.5);
    }

    private void BuildBunny(Canvas body, Canvas upper)
    {
        Brush fur = Fill("#F3F1EE");
        Brush pink = Fill("#F7B7C5");
        AddLegs(body, fur);
        upper.Children.Add(Oval(0.5, 12, 5, 5, fur));                      // tail
        upper.Children.Add(Oval(3, 12, 16, 9, fur));                       // body
        upper.Children.Add(Oval(15, -2, 3.6, 10, fur));                    // ears
        upper.Children.Add(Oval(20, -2, 3.6, 10, fur));
        upper.Children.Add(Oval(16, 0, 1.6, 6, pink));
        upper.Children.Add(Oval(21, 0, 1.6, 6, pink));
        upper.Children.Add(Oval(13, 6, 12, 10, fur));                      // head
        AddEyes(upper, 17, 21.5, 9.5);
    }

    private void BuildSlime(Canvas upper)
    {
        Brush goo = Fill("#5EDC8A");
        upper.Children.Add(Oval(3, 9, 20, 15, goo));                       // dome
        upper.Children.Add(Box(3, 17, 20, 7, goo, 3));                     // flat base
        upper.Children.Add(Oval(7, 11.5, 3.5, 2.2, Fill("#B9F5CF")));      // shine
        AddEyes(upper, 9.5, 15, 15);
    }

    private void AddLegs(Canvas body, Brush fill)
    {
        var back = Box(6, 19, 3, 5, fill);
        back.RenderTransform = _legA;
        var front = Box(14, 19, 3, 5, fill);
        front.RenderTransform = _legB;
        body.Children.Add(back);
        body.Children.Add(front);
    }

    private void AddEyes(Canvas upper, double leftX, double rightX, double y)
    {
        Brush pupil = Fill("#22252B");
        foreach (double x in new[] { leftX, rightX })
        {
            var eye = Oval(x, y, 2.1, 2.7, pupil);
            eye.RenderTransformOrigin = new Point(0.5, 0.5);
            eye.RenderTransform = _eyes;
            upper.Children.Add(eye);
        }
    }

    // ---- shape helpers (also used by the playground for food and toys) ----

    internal static Brush Fill(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal static Ellipse Oval(double x, double y, double w, double h, Brush fill)
    {
        var shape = new Ellipse { Width = w, Height = h, Fill = fill };
        SetLeft(shape, x);
        SetTop(shape, y);
        return shape;
    }

    internal static Rectangle Box(double x, double y, double w, double h, Brush fill, double radius = 1.5)
    {
        var shape = new Rectangle { Width = w, Height = h, Fill = fill, RadiusX = radius, RadiusY = radius };
        SetLeft(shape, x);
        SetTop(shape, y);
        return shape;
    }

    internal static Polygon Tri(Brush fill, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        return new Polygon
        {
            Fill = fill,
            Points = new PointCollection { new Point(x1, y1), new Point(x2, y2), new Point(x3, y3) }
        };
    }
}
