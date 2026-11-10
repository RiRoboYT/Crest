using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest
{
    /// <summary>Mouse, gestures, the Win+Alt+N hotkey, drag &amp; drop onto the shelf and typing notes.</summary>
    public partial class MainWindow
    {
        private bool _typing;          // the notes box has keyboard focus
        private bool _dragActive;      // files are being dragged over the notch
        private bool _dragOut;         // a file is being dragged out of the shelf
        private bool _hotkeyPinned;    // opened by hotkey / bubble / pop-up click: stay open until the mouse leaves
        private bool _suppressNextClick;
        private int _hWheel;
        private DateTime _lastSkip = DateTime.MinValue;
        private DispatcherTimer? _dragLeaveTimer;

        private void InitInput()
        {
            // DragLeave also fires when moving between child elements; only treat it as "left"
            // when no DragEnter follows shortly after.
            _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _dragLeaveTimer.Tick += (_, _) =>
            {
                _dragLeaveTimer.Stop();
                _dragActive = false;
                RenderShelf();
                if (!_hotkeyPinned && !_previewPinned && !_typing) SetState(NotchState.Collapsed, animate: true);
            };

            Deactivated += (_, _) => { if (_typing) EndTyping(collapseIfOutside: true); };
        }

        // =====================================================================
        //  Hover
        // =====================================================================

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            UpdateNightDim(animate: true);
            _collapseTimer.Stop();
            _popupTimer.Stop();   // keep a peek / pop-up open while you're reading it

            if (!_settings.ExpandOnHover || _state == NotchState.Expanded) return;

            // Hover intent: only open if the pointer actually rests on the notch.
            if (_settings.HoverDelayMs <= 0)
            {
                SetState(NotchState.Expanded, animate: true);
            }
            else
            {
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverDelayMs);
                _hoverTimer.Stop();
                _hoverTimer.Start();
            }
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateNightDim(animate: true)), System.Windows.Threading.DispatcherPriority.Background);
            _hoverTimer.Stop();
            _hotkeyPinned = false;   // opened by hotkey / click: closes once you've been in and left
            if (_previewPinned || _typing || _dragActive || _dragOut) return;

            if (_state == NotchState.Peek || _state == NotchState.Activity)
            {
                _popupTimer.Interval = TimeSpan.FromMilliseconds(1200);
                _popupTimer.Start();
                return;
            }

            if (_state != NotchState.Expanded) return;

            if (_settings.CollapseDelayMs <= 0)
            {
                SetState(NotchState.Collapsed, animate: true);
            }
            else
            {
                _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelayMs);
                _collapseTimer.Start();
            }
        }

        // =====================================================================
        //  Clicks & gestures
        // =====================================================================

        /// <summary>True when the click landed on a control that handles clicks itself.</summary>
        private bool IsInteractive(object source)
        {
            var d = source as DependencyObject;
            while (d != null && !ReferenceEquals(d, Notch))
            {
                if (d is ButtonBase or Slider or TextBoxBase or Thumb or ScrollBar) return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void Notch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Double-click anywhere that isn't a button → play / pause.
            if (e.ClickCount == 2 && _settings.DoubleClickPlayPause && Media.HasSession && !IsInteractive(e.OriginalSource))
            {
                e.Handled = true;
                _suppressNextClick = true;
                _ = Media.TogglePlayPauseAsync();
            }
        }

        private void Notch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                return;
            }

            // With hover-expansion disabled, a click toggles the player instead.
            if (_settings.ExpandOnHover || ProgressTrack.IsMouseOver || IsInteractive(e.OriginalSource)) return;
            SetState(_state == NotchState.Expanded ? NotchState.Collapsed : NotchState.Expanded, animate: true);
        }

        private void Notch_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_typing) return;   // keep the text box's own menu while writing a note
            e.Handled = true;
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Notch_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // Middle-click → mute / unmute.
            if (e.ChangedButton != MouseButton.Middle || !_settings.MiddleClickMute) return;
            e.Handled = true;
            if (Volume.IsAvailable) Volume.ToggleMute();
            else NativeMethods.PressKey(NativeMethods.VK_VOLUME_MUTE);
        }

        private void Notch_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Shift + wheel behaves like a sideways scroll (skip tracks / switch pages).
            if (NativeMethods.IsShiftDown())
            {
                e.Handled = true;
                OnHorizontalWheel(-e.Delta);
                return;
            }

            if (!_settings.ScrollChangesVolume) return;
            e.Handled = true;

            if (Volume.IsAvailable && (_state == NotchState.Expanded || _settings.VolumeHud))
            {
                // Open player: the slider shows the change. Collapsed: our own indicator does.
                if (_state == NotchState.Expanded) SuppressVolumeHud();
                Volume.Step(e.Delta > 0 ? 0.02f : -0.02f);
            }
            else
            {
                // Fall back to media keys so Windows shows its own volume indicator.
                NativeMethods.PressKey(e.Delta > 0 ? NativeMethods.VK_VOLUME_UP : NativeMethods.VK_VOLUME_DOWN);
            }
        }

        /// <summary>Sideways scroll (tilt wheel / touchpad): skip tracks, or switch pages when open.</summary>
        private void OnHorizontalWheel(int delta)
        {
            if (!Notch.IsMouseOver && _state != NotchState.Expanded) return;

            if (Math.Sign(delta) != Math.Sign(_hWheel)) _hWheel = 0;   // direction changed
            _hWheel += delta;
            if (Math.Abs(_hWheel) < 120) return;
            int direction = Math.Sign(_hWheel);
            _hWheel = 0;

            if (_state == NotchState.Expanded)
            {
                SwitchPage(direction);
                return;
            }

            if (!_settings.ScrollSkipsTracks || !Media.HasSession) return;
            if (DateTime.UtcNow - _lastSkip < TimeSpan.FromMilliseconds(600)) return;   // one skip per swipe
            _lastSkip = DateTime.UtcNow;
            if (direction > 0) _ = Media.NextAsync();
            else _ = Media.PreviousAsync();
        }

        /// <summary>Win + Alt + N.</summary>
        public void ToggleFromHotkey()
        {
            if (!IsEffectivelyVisible) return;
            if (_state == NotchState.Expanded)
            {
                _hotkeyPinned = false;
                SetState(NotchState.Collapsed, animate: true);
            }
            else
            {
                _hotkeyPinned = true;
                SetState(NotchState.Expanded, animate: true);
                EnterKeyboardMode();   // arrows, Space and Esc now drive the notch
            }
        }

        // =====================================================================
        //  Drag files onto the notch → shelf
        // =====================================================================

        private bool AcceptsDrop(DragEventArgs e)
            => !_dragOut && _settings.PageShelf && e.Data.GetDataPresent(DataFormats.FileDrop);

        private void Notch_DragEnter(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!AcceptsDrop(e))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            _dragLeaveTimer?.Stop();
            e.Effects = DragDropEffects.Copy;
            if (_dragActive) return;

            _dragActive = true;
            if (_currentPage != "Shelf") SelectPage("Shelf", 1);
            if (_state != NotchState.Expanded) SetState(NotchState.Expanded, animate: true);
            RenderShelf();
        }

        private void Notch_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = AcceptsDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
            _dragLeaveTimer?.Stop();
        }

        private void Notch_DragLeave(object sender, DragEventArgs e)
        {
            if (!_dragActive) return;
            _dragLeaveTimer?.Stop();
            _dragLeaveTimer?.Start();
        }

        private void Notch_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _dragLeaveTimer?.Stop();
            bool accepted = AcceptsDrop(e);
            _dragActive = false;

            if (accepted && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                _svc.Shelf.Add(files);
                _hotkeyPinned = true;   // stay open so you can see what landed; closes when you move away
            }
            RenderShelf();
        }

        // =====================================================================
        //  Typing in the notes box
        // =====================================================================

        /// <summary>The notch normally never takes focus; while typing a note it must.</summary>
        /// <summary>Gives a text box in the notch (note, to-do, calculator) real keyboard focus.</summary>
        private void BeginTyping(TextBox? box = null)
        {
            box ??= NotesBox;
            if (!_typing)
            {
                _typing = true;
                SetNoActivate(false);
                Activate();
            }
            box.Focus();
            Keyboard.Focus(box);
        }

        private void EndTyping(bool collapseIfOutside)
        {
            if (!_typing) return;
            _typing = false;
            Keyboard.ClearFocus();
            SetNoActivate(true);
            _svc.Notes.Flush();
            if (collapseIfOutside && !Notch.IsMouseOver && !_previewPinned)
                SetState(NotchState.Collapsed, animate: true);
        }
    }
}
