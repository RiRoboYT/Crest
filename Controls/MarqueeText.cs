using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Crest.Controls
{
    /// <summary>
    /// Single-line text that scrolls slowly (pause → scroll → loop) when it doesn't fit.
    /// Font/colour are inherited, so set TextElement.* on it or a parent.
    ///
    /// Scrolling only runs while <see cref="IsActive"/> is true (i.e. while the notch is open), so
    /// it costs nothing when collapsed. With <see cref="IsScrollingEnabled"/> false it falls back to "…".
    /// </summary>
    public sealed class MarqueeText : FrameworkElement
    {
        private const double Gap = 48;          // space between the end of the text and its repeat
        private const double Speed = 32;        // px per second
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.8);

        private readonly StackPanel _panel;
        private readonly TextBlock _first;
        private readonly TextBlock _second;
        private readonly TranslateTransform _shift = new();
        private double _runningDistance = -1;

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MarqueeText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChanged));

        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(MarqueeText),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange, (d, _) => ((MarqueeText)d).ResetAnimation()));

        public static readonly DependencyProperty IsScrollingEnabledProperty = DependencyProperty.Register(
            nameof(IsScrollingEnabled), typeof(bool), typeof(MarqueeText),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure, OnScrollingEnabledChanged));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public bool IsScrollingEnabled
        {
            get => (bool)GetValue(IsScrollingEnabledProperty);
            set => SetValue(IsScrollingEnabledProperty, value);
        }

        public MarqueeText()
        {
            _first = new TextBlock { TextWrapping = TextWrapping.NoWrap };
            _second = new TextBlock { TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(Gap, 0, 0, 0), Opacity = 0 };
            _panel = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = _shift };
            _panel.Children.Add(_first);
            _panel.Children.Add(_second);

            AddVisualChild(_panel);
            AddLogicalChild(_panel);

            ClipToBounds = true;
            IsHitTestVisible = false;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _panel;
        protected override IEnumerator LogicalChildren => new[] { _panel }.GetEnumerator();

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var m = (MarqueeText)d;
            string text = e.NewValue as string ?? "";
            m._first.Text = text;
            m._second.Text = text;
            m.ResetAnimation();
        }

        private static void OnScrollingEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var m = (MarqueeText)d;
            bool enabled = (bool)e.NewValue;
            m._first.TextTrimming = enabled ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            m._second.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            m.ResetAnimation();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _panel.Measure(IsScrollingEnabled
                ? new Size(double.PositiveInfinity, availableSize.Height)
                : availableSize);

            double textWidth = _first.DesiredSize.Width;
            double width = double.IsInfinity(availableSize.Width) ? textWidth : Math.Min(textWidth, availableSize.Width);
            return new Size(width, _panel.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double panelWidth = IsScrollingEnabled ? _panel.DesiredSize.Width : finalSize.Width;
            _panel.Arrange(new Rect(0, 0, panelWidth, finalSize.Height));
            UpdateAnimation(finalSize.Width);
            return finalSize;
        }

        private void ResetAnimation()
        {
            _runningDistance = -1;
            InvalidateArrange();
        }

        private void UpdateAnimation(double visibleWidth)
        {
            double textWidth = _first.DesiredSize.Width;
            bool overflow = textWidth - visibleWidth > 1;
            bool shouldScroll = IsScrollingEnabled && IsActive && overflow;

            if (!shouldScroll)
            {
                if (_runningDistance != 0)
                {
                    _shift.BeginAnimation(TranslateTransform.XProperty, null);
                    _shift.X = 0;
                    _second.Opacity = 0;
                    _runningDistance = 0;
                }
                return;
            }

            double distance = textWidth + Gap;
            if (Math.Abs(distance - _runningDistance) < 0.5) return;   // already scrolling this text
            _runningDistance = distance;

            _second.Opacity = 1;

            var scroll = TimeSpan.FromSeconds(distance / Speed);
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = Pause + scroll,
                RepeatBehavior = RepeatBehavior.Forever
            };
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(Pause)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(Pause + scroll)));
            anim.Freeze();
            _shift.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }
}
