using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ClipThat.UI;

/// <summary>
/// Enables frame-rate-independent smooth inertial mouse-wheel scrolling globally across every
/// <see cref="ScrollViewer"/> and its WPF-UI subclasses (<c>PassiveScrollViewer</c>, <c>DynamicScrollViewer</c>)
/// without modifying control templates, with zero idle CPU usage and deterministic cleanup.
/// </summary>
public static class SmoothScrollBehavior
{
    private const double PixelsPerWheelLine = 48.0;
    private const double SmoothingRate = 18.0;
    private const double SnapThresholdPixels = 0.5;
    private const double ExternalScrollTolerancePixels = 2.0;
    private const double MinFrameDeltaSeconds = 0.001;
    private const double MaxFrameDeltaSeconds = 0.05;

    private static int _globallyRegistered;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(true, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(SmoothScrollState),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject obj)
        => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value)
        => obj.SetValue(IsEnabledProperty, value);

    /// <summary>
    /// Registers a WPF class handler on <see cref="ScrollViewer"/> once per application process so every
    /// page scroll surface and pixel-scrolling virtualized list receives smooth inertial scrolling automatically.
    /// </summary>
    public static void RegisterGlobally()
    {
        if (Interlocked.Exchange(ref _globallyRegistered, 1) != 0)
        {
            return;
        }

        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnClassPreviewMouseWheel),
            handledEventsToo: false);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer && e.NewValue is false &&
            scrollViewer.GetValue(StateProperty) is SmoothScrollState existing)
        {
            existing.Detach();
            scrollViewer.ClearValue(StateProperty);
        }
    }

    private static void OnClassPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer scrollViewer || !GetIsEnabled(scrollViewer))
        {
            return;
        }

        if (scrollViewer.GetValue(StateProperty) is not SmoothScrollState state)
        {
            state = new SmoothScrollState(scrollViewer);
            scrollViewer.SetValue(StateProperty, state);
            state.AttachLifecycleHooks();
        }

        state.HandlePreviewMouseWheel(e);
    }

    internal static double ComputeWheelDeltaPixels(int wheelDelta, int wheelScrollLines, double viewportHeight)
    {
        if (wheelDelta == 0)
        {
            return 0.0;
        }

        double stepPerNotch = wheelScrollLines < 0
            ? Math.Max(64.0, viewportHeight * 0.85)
            : Math.Clamp(wheelScrollLines, 1, 10) * PixelsPerWheelLine;

        return -(wheelDelta / 120.0) * stepPerNotch;
    }

    internal static (double NextOffset, bool ReachedTarget) AdvanceTowardsTarget(
        double currentOffset,
        double targetOffset,
        double deltaSeconds)
    {
        double remaining = targetOffset - currentOffset;
        if (Math.Abs(remaining) <= SnapThresholdPixels)
        {
            return (targetOffset, true);
        }

        double clampedDt = Math.Clamp(deltaSeconds, MinFrameDeltaSeconds, MaxFrameDeltaSeconds);
        double alpha = 1.0 - Math.Exp(-SmoothingRate * clampedDt);
        double nextOffset = currentOffset + (remaining * alpha);

        if (Math.Abs(targetOffset - nextOffset) <= SnapThresholdPixels)
        {
            return (targetOffset, true);
        }

        return (nextOffset, false);
    }

    private static bool SupportsPixelScrolling(ScrollViewer scrollViewer)
    {
        if (!scrollViewer.CanContentScroll)
        {
            return true;
        }

        return scrollViewer.TemplatedParent is DependencyObject templatedParent &&
               VirtualizingPanel.GetScrollUnit(templatedParent) == ScrollUnit.Pixel;
    }

    private static bool IsInsideScrollBar(DependencyObject? source, ScrollViewer boundary)
    {
        DependencyObject? current = source;
        while (current is not null && !ReferenceEquals(current, boundary))
        {
            if (current is ScrollBar)
            {
                return true;
            }

            current = GetParentSafe(current);
        }

        return false;
    }

    private static bool HasScrollableInnerDescendant(ScrollViewer outerScrollViewer, object? originalSource, int wheelDelta)
    {
        DependencyObject? current = originalSource as DependencyObject;
        if (current is null)
        {
            return false;
        }

        while (current is not null && !ReferenceEquals(current, outerScrollViewer))
        {
            if (current is ScrollViewer inner && inner.ScrollableHeight > 0.5)
            {
                bool scrollingUp = wheelDelta > 0;
                bool canScrollInDirection = scrollingUp
                    ? inner.VerticalOffset > 0.5
                    : inner.VerticalOffset < inner.ScrollableHeight - 0.5;
                if (canScrollInDirection)
                {
                    return true;
                }
            }

            current = GetParentSafe(current);
        }

        return false;
    }

    private static DependencyObject? GetParentSafe(DependencyObject current)
    {
        DependencyObject? parent = current is Visual or Visual3D
            ? VisualTreeHelper.GetParent(current)
            : null;
        return parent ?? LogicalTreeHelper.GetParent(current);
    }

    private sealed class SmoothScrollState(ScrollViewer scrollViewer)
    {
        private readonly ScrollViewer _scrollViewer = scrollViewer;
        private double _currentOffset;
        private double _targetOffset;
        private double _previousAppliedOffset;
        private double _lastAppliedOffset;
        private long _lastFrameTimestamp;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
        private bool _isAnimating;

        public void AttachLifecycleHooks()
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
            _scrollViewer.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            _scrollViewer.Unloaded += OnUnloaded;
            _scrollViewer.IsVisibleChanged += OnIsVisibleChanged;
        }

        public void Detach()
        {
            StopAnimation();
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            _scrollViewer.Unloaded -= OnUnloaded;
            _scrollViewer.IsVisibleChanged -= OnIsVisibleChanged;
        }

        public void HandlePreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0)
            {
                return;
            }

            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            {
                return;
            }

            double maxOffset = _scrollViewer.ScrollableHeight;
            if (maxOffset <= 0.5 || !SupportsPixelScrolling(_scrollViewer))
            {
                return;
            }

            if (HasScrollableInnerDescendant(_scrollViewer, e.OriginalSource, e.Delta))
            {
                return;
            }

            double deltaPixels = ComputeWheelDeltaPixels(
                e.Delta,
                SystemParameters.WheelScrollLines,
                _scrollViewer.ViewportHeight);
            if (Math.Abs(deltaPixels) < 0.01)
            {
                return;
            }

            double anchor = _isAnimating ? _targetOffset : _scrollViewer.VerticalOffset;
            if ((deltaPixels < 0.0 && anchor <= 0.01) ||
                (deltaPixels > 0.0 && anchor >= maxOffset - 0.01))
            {
                // Already at the boundary; let parent ScrollViewers handle further scrolling if nested.
                return;
            }

            if (!_isAnimating)
            {
                _currentOffset = _scrollViewer.VerticalOffset;
                _targetOffset = _currentOffset;
                _previousAppliedOffset = _currentOffset;
                _lastAppliedOffset = _currentOffset;
            }
            else if ((_targetOffset - _currentOffset) * deltaPixels < 0.0)
            {
                // Reverse direction immediately without fighting leftover opposite inertia.
                _targetOffset = _currentOffset;
            }

            _targetOffset = Math.Clamp(_targetOffset + deltaPixels, 0.0, maxOffset);
            e.Handled = true;
            StartAnimation();
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isAnimating && IsInsideScrollBar(e.OriginalSource as DependencyObject, _scrollViewer))
            {
                // Immediately yield when the user grabs the scrollbar thumb or clicks the scrollbar track.
                StopAnimation();
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_isAnimating || Math.Abs(e.VerticalChange) <= 0.001)
            {
                return;
            }

            // WPF fires ScrollChanged asynchronously during the layout pass after ScrollToVerticalOffset().
            // Compare the reported offset against our applied range instead of a synchronous flag so we
            // never misclassify our own animation frame as an external scroll.
            if (Math.Abs(e.ExtentHeightChange) > 0.5 || Math.Abs(e.ViewportHeightChange) > 0.5)
            {
                double maxOffset = _scrollViewer.ScrollableHeight;
                _currentOffset = Math.Clamp(_currentOffset, 0.0, maxOffset);
                _targetOffset = Math.Clamp(_targetOffset, 0.0, maxOffset);
                return;
            }

            double minExpected = Math.Min(_previousAppliedOffset, _lastAppliedOffset) - ExternalScrollTolerancePixels;
            double maxExpected = Math.Max(_previousAppliedOffset, _lastAppliedOffset) + ExternalScrollTolerancePixels;
            if (e.VerticalOffset < minExpected || e.VerticalOffset > maxExpected)
            {
                // External jump (scrollbar drag, keyboard PageUp/PageDown/Home/End, or programmatic scroll).
                StopAnimation();
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is false)
            {
                StopAnimation();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopAnimation();
        }

        private void StartAnimation()
        {
            if (_isAnimating)
            {
                return;
            }

            _isAnimating = true;
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            _lastRenderingTime = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopAnimation()
        {
            if (!_isAnimating)
            {
                return;
            }

            _isAnimating = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isAnimating || !_scrollViewer.IsLoaded || !_scrollViewer.IsVisible)
            {
                StopAnimation();
                return;
            }

            if (e is RenderingEventArgs renderingArgs)
            {
                if (renderingArgs.RenderingTime == _lastRenderingTime)
                {
                    return;
                }

                _lastRenderingTime = renderingArgs.RenderingTime;
            }

            long now = Stopwatch.GetTimestamp();
            double dt = _lastFrameTimestamp != 0
                ? Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds
                : 1.0 / 60.0;
            _lastFrameTimestamp = now;

            double maxOffset = _scrollViewer.ScrollableHeight;
            _targetOffset = Math.Clamp(_targetOffset, 0.0, maxOffset);
            _currentOffset = Math.Clamp(_currentOffset, 0.0, maxOffset);

            (double nextOffset, bool reachedTarget) = AdvanceTowardsTarget(_currentOffset, _targetOffset, dt);
            _currentOffset = nextOffset;
            ApplyOffset(nextOffset);

            if (reachedTarget)
            {
                StopAnimation();
            }
        }

        private void ApplyOffset(double offset)
        {
            _previousAppliedOffset = _lastAppliedOffset;
            _lastAppliedOffset = offset;
            _scrollViewer.ScrollToVerticalOffset(offset);
        }
    }
}
