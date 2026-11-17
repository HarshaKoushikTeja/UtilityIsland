using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicIsland.Pets;

/// <summary>
/// The strip in the expanded island where you interact with the pet:
/// pet it, drop food for it to walk over and eat, or throw a ball for it to chase.
/// </summary>
public sealed class PetPlayground : Border
{
    private const double PetScale = 1.5;
    private const double BallSize = 8;
    private const double FoodSize = 12;

    private readonly Canvas _stage = new() { ClipToBounds = true };
    private readonly PetControl _pet = new() { Scale = PetScale };
    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();

    // Food
    private Canvas? _food;
    private double _foodX;
    private double _foodY;
    private bool _foodLanded;
    private double _eatLeft;

    // Ball
    private Ellipse? _ball;
    private double _ballX;
    private double _ballY;
    private double _ballVx;
    private double _ballVy;
    private double _playLeft;
    private double _kickCooldown;
    private int _kicks;

    public event Action? Petted;
    public event Action? Fed;
    public event Action? Played;

    public PetControl Pet => _pet;

    /// <summary>True while food is out or a game is running.</summary>
    public bool Busy => _food != null || _ball != null;

    public bool Paused { get; set; }

    public PetPlayground()
    {
        Height = 58;
        CornerRadius = new CornerRadius(14);
        Background = PetControl.Fill("#14FFFFFF");
        Child = _stage;

        _stage.Children.Add(_pet);
        _stage.SizeChanged += (_, _) => Canvas.SetTop(_pet, FloorY - PetControl.PetHeight * PetScale);
        _pet.Clicked += PetIt;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Step(0.033);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
    }

    private double FloorY => Math.Max(0, _stage.ActualHeight - 3);

    // ---- actions -------------------------------------------------------

    public void PetIt()
    {
        _pet.ShowLove();
        Petted?.Invoke();
    }

    /// <summary>Drops a treat somewhere on the floor; the pet walks over and eats it.</summary>
    public bool Feed()
    {
        double width = _stage.ActualWidth;
        if (Busy || width < 80) return false;

        _foodX = 20 + _random.NextDouble() * (width - 40);
        _foodY = -FoodSize;
        _foodLanded = false;
        _eatLeft = 0;

        _food = MakeFood(_pet.Kind);
        _stage.Children.Add(_food);
        PlaceFood();

        _pet.SeekX = _foodX;
        return true;
    }

    /// <summary>Throws a ball; the pet chases it and knocks it around for a few seconds.</summary>
    public bool Play()
    {
        double width = _stage.ActualWidth;
        if (Busy || width < 80) return false;

        _ballX = Math.Clamp(_pet.CenterX, 10, width - 20);
        _ballY = FloorY - BallSize - 14;
        _ballVx = (_random.NextDouble() < 0.5 ? -1 : 1) * 120;
        _ballVy = -150;
        _playLeft = 8;
        _kickCooldown = 0.4;
        _kicks = 0;

        _ball = new Ellipse
        {
            Width = BallSize,
            Height = BallSize,
            Fill = PetControl.Fill("#FFFF6482"),
            IsHitTestVisible = false
        };
        _stage.Children.Add(_ball);
        PlaceBall();
        return true;
    }

    // ---- simulation ----------------------------------------------------

    private void Step(double dt)
    {
        if (Paused) return;
        if (_food != null) StepFood(dt);
        if (_ball != null) StepBall(dt);
    }

    private void StepFood(double dt)
    {
        if (_food == null) return;

        if (!_foodLanded)
        {
            _foodY += 150 * dt;
            double rest = FloorY - FoodSize;
            if (_foodY >= rest)
            {
                _foodY = rest;
                _foodLanded = true;
            }
            PlaceFood();
            return;
        }

        if (_eatLeft > 0)
        {
            _eatLeft -= dt;
            _food.Opacity = Math.Max(0, _eatLeft / 0.7);
            if (_eatLeft <= 0)
            {
                _stage.Children.Remove(_food);
                _food = null;
                _pet.SeekX = null;
                _pet.ShowLove();
                Fed?.Invoke();
            }
            return;
        }

        if (Math.Abs(_pet.CenterX - _foodX) < 8)
        {
            _eatLeft = 0.7;
            _pet.Chomp(0.7);
        }
    }

    private void StepBall(double dt)
    {
        if (_ball == null) return;

        double width = _stage.ActualWidth;
        double rest = FloorY - BallSize;

        _ballVy += 420 * dt; // gravity
        _ballX += _ballVx * dt;
        _ballY += _ballVy * dt;

        if (_ballY > rest)
        {
            _ballY = rest;
            _ballVy = -_ballVy * 0.55;
            if (Math.Abs(_ballVy) < 25) _ballVy = 0;
            _ballVx *= 0.93;
        }
        if (_ballX < 4)
        {
            _ballX = 4;
            _ballVx = Math.Abs(_ballVx);
        }
        if (_ballX > width - BallSize - 4)
        {
            _ballX = width - BallSize - 4;
            _ballVx = -Math.Abs(_ballVx);
        }
        PlaceBall();

        double ballCentre = _ballX + BallSize / 2;
        _pet.SeekX = ballCentre;

        // When the pet catches up with the ball near the ground, it boots it toward the far side.
        _kickCooldown -= dt;
        if (_kickCooldown <= 0 && Math.Abs(_pet.CenterX - ballCentre) < 10 && _ballY > rest - 8)
        {
            double away = ballCentre >= width / 2 ? -1 : 1;
            _ballVx = away * (110 + _random.NextDouble() * 70);
            _ballVy = -(120 + _random.NextDouble() * 60);
            _kickCooldown = 0.45;
            _kicks++;
        }

        _playLeft -= dt;
        if (_playLeft <= 0 || _kicks >= 5)
        {
            _stage.Children.Remove(_ball);
            _ball = null;
            _pet.SeekX = null;
            _pet.ShowLove();
            Played?.Invoke();
        }
    }

    private void PlaceFood()
    {
        if (_food == null) return;
        Canvas.SetLeft(_food, _foodX - FoodSize / 2);
        Canvas.SetTop(_food, _foodY);
    }

    private void PlaceBall()
    {
        if (_ball == null) return;
        Canvas.SetLeft(_ball, _ballX);
        Canvas.SetTop(_ball, _ballY);
    }

    // ---- treats, one per pet --------------------------------------------

    private static Canvas MakeFood(PetKind kind)
    {
        var food = new Canvas { Width = FoodSize, Height = FoodSize, IsHitTestVisible = false };

        switch (kind)
        {
            case PetKind.Cat: // fish
                Brush fish = PetControl.Fill("#7FC8F8");
                food.Children.Add(PetControl.Oval(0, 3, 9, 6, fish));
                food.Children.Add(PetControl.Tri(fish, 7.5, 6, 12, 2, 12, 10));
                break;

            case PetKind.Dog: // bone
                Brush bone = PetControl.Fill("#F5EFE6");
                food.Children.Add(PetControl.Box(2, 4.5, 8, 3, bone));
                food.Children.Add(PetControl.Oval(0, 2.5, 3.5, 3.5, bone));
                food.Children.Add(PetControl.Oval(0, 6, 3.5, 3.5, bone));
                food.Children.Add(PetControl.Oval(8.5, 2.5, 3.5, 3.5, bone));
                food.Children.Add(PetControl.Oval(8.5, 6, 3.5, 3.5, bone));
                break;

            case PetKind.Fox: // berry
                food.Children.Add(PetControl.Oval(2, 3.5, 8, 8, PetControl.Fill("#9B5DE5")));
                food.Children.Add(PetControl.Oval(5, 1, 4, 3, PetControl.Fill("#5EDC8A")));
                break;

            case PetKind.Bunny: // carrot
                food.Children.Add(PetControl.Tri(PetControl.Fill("#FF9F45"), 2, 3, 10, 3, 6, 12));
                food.Children.Add(PetControl.Box(5, 0, 2, 4, PetControl.Fill("#5EDC8A"), 1));
                break;

            default: // candy drop for the slime
                food.Children.Add(PetControl.Oval(2, 3, 8, 8, PetControl.Fill("#FFD166")));
                food.Children.Add(PetControl.Oval(4, 4.5, 2.5, 2, PetControl.Fill("#FFF1C1")));
                break;
        }
        return food;
    }
}
