using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClipThat.UI.Controls;

/// <summary>
/// Unified single-bar video playback and trim timeline with dimmed excluded regions,
/// bright accent selected range bar, interactive playhead pin, and two draggable
/// edge handles (<c>Początek</c> and <c>Koniec</c>) enforcing a 0.5-second minimum span.
/// </summary>
public partial class RangeTrimSlider : UserControl
{
    public const double MinimumGapSeconds = 0.5;
    private const double HandleWidth = 14.0;

    private static readonly Brush PlayheadNormalFillBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Brush PlayheadNormalStrokeBrush = CreateFrozenBrush(0xCC, 0x09, 0x09, 0x0B);
    private static readonly Brush PlayheadScrubbingStrokeBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0xFF);

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(
            nameof(Minimum),
            typeof(double),
            typeof(RangeTrimSlider),
            new FrameworkPropertyMetadata(0.0, OnRangePropertyChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(RangeTrimSlider),
            new FrameworkPropertyMetadata(1.0, OnRangePropertyChanged));

    public static readonly DependencyProperty StartValueProperty =
        DependencyProperty.Register(
            nameof(StartValue),
            typeof(double),
            typeof(RangeTrimSlider),
            new FrameworkPropertyMetadata(
                0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnRangePropertyChanged));

    public static readonly DependencyProperty EndValueProperty =
        DependencyProperty.Register(
            nameof(EndValue),
            typeof(double),
            typeof(RangeTrimSlider),
            new FrameworkPropertyMetadata(
                1.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnRangePropertyChanged));

    public static readonly DependencyProperty CurrentPositionProperty =
        DependencyProperty.Register(
            nameof(CurrentPosition),
            typeof(double),
            typeof(RangeTrimSlider),
            new FrameworkPropertyMetadata(0.0, OnRangePropertyChanged));

    private double _dragEdgeOffsetX;
    private bool _isBatchUpdating;
    private bool _isScrubbingTrack;

    public RangeTrimSlider()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisuals();
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double StartValue
    {
        get => (double)GetValue(StartValueProperty);
        set => SetValue(StartValueProperty, value);
    }

    public double EndValue
    {
        get => (double)GetValue(EndValueProperty);
        set => SetValue(EndValueProperty, value);
    }

    public double CurrentPosition
    {
        get => (double)GetValue(CurrentPositionProperty);
        set => SetValue(CurrentPositionProperty, value);
    }

    /// <summary>
    /// Raised in real time while the user drags the left (<c>Początek</c>) handle.
    /// </summary>
    public event EventHandler<double>? StartChanged;

    /// <summary>
    /// Raised in real time while the user drags the right (<c>Koniec</c>) handle.
    /// </summary>
    public event EventHandler<double>? EndChanged;

    /// <summary>
    /// Raised when the user presses the track surface to begin seeking/scrubbing the playhead.
    /// </summary>
    public event EventHandler? PositionSeekStarted;

    /// <summary>
    /// Raised when the user clicks or drags on the track surface to seek the playhead.
    /// </summary>
    public event EventHandler<double>? PositionSeekRequested;

    /// <summary>
    /// Raised when the user finishes scrubbing the playhead on the track surface.
    /// </summary>
    public event EventHandler<double>? PositionSeekCompleted;

    /// <summary>
    /// Raised when the user begins dragging either trim handle.
    /// </summary>
    public event EventHandler? ThumbDragStarted;

    /// <summary>
    /// Raised when the user finishes dragging either trim handle.
    /// </summary>
    public event EventHandler? ThumbDragCompleted;

    /// <summary>
    /// Atomically updates all range values and re-renders once without raising user-drag events.
    /// </summary>
    public void SetRange(
        double minimum,
        double maximum,
        double startValue,
        double endValue,
        double currentPosition)
    {
        double safeMin = double.IsFinite(minimum) ? minimum : 0.0;
        double safeMax = double.IsFinite(maximum) && maximum >= safeMin ? maximum : safeMin;
        double span = safeMax - safeMin;
        double minGap = Math.Min(MinimumGapSeconds, span);

        double clampedStart = span > 0.0
            ? Math.Clamp(startValue, safeMin, Math.Max(safeMin, safeMax - minGap))
            : safeMin;

        double clampedEnd = span > 0.0
            ? Math.Clamp(endValue, Math.Min(safeMax, clampedStart + minGap), safeMax)
            : safeMax;

        double clampedPos = span > 0.0
            ? Math.Clamp(currentPosition, safeMin, safeMax)
            : safeMin;

        _isBatchUpdating = true;
        try
        {
            Minimum = safeMin;
            Maximum = safeMax;
            StartValue = clampedStart;
            EndValue = clampedEnd;
            CurrentPosition = clampedPos;
        }
        finally
        {
            _isBatchUpdating = false;
        }

        UpdateVisuals();
    }

    private static void OnRangePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RangeTrimSlider slider && !slider._isBatchUpdating)
        {
            slider.UpdateVisuals();
        }
    }

    private void TrackCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisuals();
    }

    private void StartThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!IsEnabled || Maximum <= Minimum)
        {
            return;
        }

        // StartThumb's right edge aligns with startX, so preserve the pointer offset from that edge.
        double pointerInThumbX = Mouse.GetPosition(StartThumb).X;
        _dragEdgeOffsetX = HandleWidth - pointerInThumbX;
        ThumbDragStarted?.Invoke(this, EventArgs.Empty);
    }

    private void StartThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!IsEnabled || Maximum <= Minimum)
        {
            return;
        }

        double mouseX = Mouse.GetPosition(TrackCanvas).X;
        double targetStartX = mouseX + _dragEdgeOffsetX;
        double rawValue = PixelToValue(targetStartX);

        double span = Maximum - Minimum;
        double minGap = Math.Min(MinimumGapSeconds, span);
        double maxAllowedStart = Math.Max(Minimum, EndValue - minGap);
        double clampedStart = Math.Clamp(rawValue, Minimum, maxAllowedStart);

        bool changed = Math.Abs(clampedStart - StartValue) > 0.0001 ||
                       Math.Abs(clampedStart - CurrentPosition) > 0.0001;

        _isBatchUpdating = true;
        try
        {
            StartValue = clampedStart;
            CurrentPosition = clampedStart;
        }
        finally
        {
            _isBatchUpdating = false;
        }

        UpdateVisuals();
        if (changed)
        {
            StartChanged?.Invoke(this, clampedStart);
        }
    }

    private void EndThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!IsEnabled || Maximum <= Minimum)
        {
            return;
        }

        // EndThumb's left edge aligns with endX, so preserve the pointer offset from that edge.
        _dragEdgeOffsetX = Mouse.GetPosition(EndThumb).X;
        ThumbDragStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!IsEnabled || Maximum <= Minimum)
        {
            return;
        }

        double mouseX = Mouse.GetPosition(TrackCanvas).X;
        double targetEndX = mouseX - _dragEdgeOffsetX;
        double rawValue = PixelToValue(targetEndX);

        double span = Maximum - Minimum;
        double minGap = Math.Min(MinimumGapSeconds, span);
        double minAllowedEnd = Math.Min(Maximum, StartValue + minGap);
        double clampedEnd = Math.Clamp(rawValue, minAllowedEnd, Maximum);

        bool changed = Math.Abs(clampedEnd - EndValue) > 0.0001 ||
                       Math.Abs(clampedEnd - CurrentPosition) > 0.0001;

        _isBatchUpdating = true;
        try
        {
            EndValue = clampedEnd;
            CurrentPosition = clampedEnd;
        }
        finally
        {
            _isBatchUpdating = false;
        }

        UpdateVisuals();
        if (changed)
        {
            EndChanged?.Invoke(this, clampedEnd);
        }
    }

    private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ThumbDragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void TrackCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled || Maximum <= Minimum)
        {
            return;
        }

        _isScrubbingTrack = true;
        UpdatePlayheadActiveVisual();
        TrackCanvas.CaptureMouse();
        PositionSeekStarted?.Invoke(this, EventArgs.Empty);
        SeekPlayheadToPixel(e.GetPosition(TrackCanvas).X);
        e.Handled = true;
    }

    private void TrackCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isScrubbingTrack || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SeekPlayheadToPixel(e.GetPosition(TrackCanvas).X);
    }

    private void TrackCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FinishTrackScrub();
    }

    private void TrackCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        FinishTrackScrub();
    }

    private void FinishTrackScrub()
    {
        if (!_isScrubbingTrack)
        {
            return;
        }

        _isScrubbingTrack = false;
        UpdatePlayheadActiveVisual();

        if (TrackCanvas.IsMouseCaptured)
        {
            TrackCanvas.ReleaseMouseCapture();
        }

        PositionSeekCompleted?.Invoke(this, CurrentPosition);
    }

    private void UpdatePlayheadActiveVisual()
    {
        if (PlayheadHead is null)
        {
            return;
        }

        if (_isScrubbingTrack)
        {
            PlayheadHead.SetResourceReference(Shape.FillProperty, "SystemAccentColorPrimaryBrush");
            PlayheadHead.Stroke = PlayheadScrubbingStrokeBrush;
        }
        else
        {
            PlayheadHead.Fill = PlayheadNormalFillBrush;
            PlayheadHead.Stroke = PlayheadNormalStrokeBrush;
        }
    }

    private void SeekPlayheadToPixel(double x)
    {
        if (Maximum <= Minimum)
        {
            return;
        }

        double targetPos = Math.Clamp(PixelToValue(x), Minimum, Maximum);
        CurrentPosition = targetPos;
        PositionSeekRequested?.Invoke(this, targetPos);
    }

    private double PixelToValue(double x)
    {
        double usableWidth = TrackCanvas.ActualWidth - (HandleWidth * 2.0);
        if (usableWidth <= 0.0 || Maximum <= Minimum)
        {
            return Minimum;
        }

        double ratio = Math.Clamp((x - HandleWidth) / usableWidth, 0.0, 1.0);
        return Minimum + (ratio * (Maximum - Minimum));
    }

    private void UpdateVisuals()
    {
        if (TrackCanvas is null)
        {
            return;
        }

        double canvasWidth = TrackCanvas.ActualWidth;
        double canvasHeight = TrackCanvas.ActualHeight;
        double usableWidth = canvasWidth - (HandleWidth * 2.0);
        if (usableWidth <= 1.0 || canvasHeight <= 1.0)
        {
            return;
        }

        double trackLeft = HandleWidth;
        double trackRight = trackLeft + usableWidth;
        double range = Maximum - Minimum;

        double startRatio;
        double endRatio;
        double playheadRatio;
        bool hasValidRange = range > 0.0;

        if (!hasValidRange)
        {
            startRatio = 0.0;
            endRatio = 1.0;
            playheadRatio = 0.0;
        }
        else
        {
            double clampedStart = Math.Clamp(StartValue, Minimum, Maximum);
            double clampedEnd = Math.Clamp(EndValue, clampedStart, Maximum);
            double clampedPos = Math.Clamp(CurrentPosition, Minimum, Maximum);

            startRatio = (clampedStart - Minimum) / range;
            endRatio = (clampedEnd - Minimum) / range;
            playheadRatio = (clampedPos - Minimum) / range;
        }

        double startX = trackLeft + (startRatio * usableWidth);
        double endX = trackLeft + (endRatio * usableWidth);
        double playheadX = trackLeft + (playheadRatio * usableWidth);

        double trackTop = (canvasHeight - FullTrackBorder.Height) / 2.0;
        Canvas.SetLeft(FullTrackBorder, trackLeft);
        Canvas.SetTop(FullTrackBorder, trackTop);
        FullTrackBorder.Width = usableWidth;

        double leftDimWidth = Math.Max(0.0, startX - trackLeft);
        Canvas.SetLeft(LeftDimOverlay, trackLeft);
        Canvas.SetTop(LeftDimOverlay, trackTop);
        LeftDimOverlay.Width = leftDimWidth;
        LeftDimOverlay.Visibility = leftDimWidth > 0.5 ? Visibility.Visible : Visibility.Collapsed;

        double rightDimWidth = Math.Max(0.0, trackRight - endX);
        Canvas.SetLeft(RightDimOverlay, endX);
        Canvas.SetTop(RightDimOverlay, trackTop);
        RightDimOverlay.Width = rightDimWidth;
        RightDimOverlay.Visibility = rightDimWidth > 0.5 ? Visibility.Visible : Visibility.Collapsed;

        double highlightTop = (canvasHeight - SelectedRangeHighlight.Height) / 2.0;
        Canvas.SetLeft(SelectedRangeHighlight, startX);
        Canvas.SetTop(SelectedRangeHighlight, highlightTop);
        SelectedRangeHighlight.Width = Math.Max(0.0, endX - startX);

        double playedEndX = Math.Clamp(playheadX, startX, endX);
        double playedWidth = hasValidRange ? Math.Max(0.0, playedEndX - startX) : 0.0;
        double playedTop = (canvasHeight - PlayedRangeHighlight.Height) / 2.0;
        Canvas.SetLeft(PlayedRangeHighlight, startX);
        Canvas.SetTop(PlayedRangeHighlight, playedTop);
        PlayedRangeHighlight.Width = playedWidth;
        PlayedRangeHighlight.Visibility = playedWidth > 0.5 ? Visibility.Visible : Visibility.Collapsed;

        double playheadTop = (canvasHeight - PlayheadIndicator.Height) / 2.0;
        Canvas.SetLeft(PlayheadIndicator, playheadX - (PlayheadIndicator.Width / 2.0));
        Canvas.SetTop(PlayheadIndicator, playheadTop);
        PlayheadIndicator.Visibility = hasValidRange ? Visibility.Visible : Visibility.Collapsed;

        double thumbTop = (canvasHeight - StartThumb.Height) / 2.0;
        Canvas.SetLeft(StartThumb, startX - HandleWidth);
        Canvas.SetTop(StartThumb, thumbTop);

        Canvas.SetLeft(EndThumb, endX);
        Canvas.SetTop(EndThumb, thumbTop);
    }

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
