using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>One remembered copy: text or an image.</summary>
    public sealed class ClipItem
    {
        public string? Text { get; init; }
        public BitmapSource? Image { get; init; }
        /// <summary>Small preview for the list.</summary>
        public BitmapSource? Thumbnail { get; init; }
        public bool IsImage => Image != null;
    }

    /// <summary>
    /// Optional clipboard history for the Notes page: the last few copied texts (and images).
    /// Kept in memory only – never written to disk – and only while enabled.
    /// </summary>
    public sealed class ClipboardService : IDisposable
    {
        private const int MaxItems = 8;
        private const int MaxImages = 3;
        private const int MaxLength = 400;
        private const int MaxImageSide = 4096;

        private readonly MessageWindow _window = new("CrestClipboard");
        private readonly Dispatcher _dispatcher;
        private readonly List<ClipItem> _items = new();
        private bool _listening;
        private string? _ownCopy;
        private bool _ownImageCopy;

        public event EventHandler? Changed;

        public IReadOnlyList<ClipItem> Items => _items;

        // ---------------------------------------------------------------- pinned texts (kept on disk because you chose them)
        private static readonly string PinsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest", "clipboard-pins.json");
        private List<string>? _pinned;

        /// <summary>Texts you pinned (signature, address, links…). Always shown first.</summary>
        public IReadOnlyList<string> Pinned
        {
            get
            {
                if (_pinned != null) return _pinned;
                try
                {
                    _pinned = System.IO.File.Exists(PinsPath)
                        ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(PinsPath))
                        : null;
                }
                catch
                {
                    _pinned = null;
                }
                return _pinned ??= new List<string>();
            }
        }

        public bool IsPinned(string text) => Pinned.Contains(text);

        public void TogglePin(string text)
        {
            var list = (List<string>)Pinned;
            if (!list.Remove(text))
            {
                list.Insert(0, text);
                if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
            }
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PinsPath)!);
                System.IO.File.WriteAllText(PinsPath, System.Text.Json.JsonSerializer.Serialize(list));
            }
            catch (Exception ex)
            {
                App.LogError("Saving clipboard pins failed", ex);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Also remember copied images (screenshots, pictures from the web…).</summary>
        public bool ImagesEnabled { get; set; } = true;

        public ClipboardService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _window.Message += (msg, _, _) =>
            {
                if (msg != NativeMethods.WM_CLIPBOARDUPDATE) return false;
                // Read a moment later: the copying app may still hold the clipboard open.
                _dispatcher.InvokeAsync(ReadClipboard, DispatcherPriority.Background);
                return true;
            };
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled == _listening) return;
            if (enabled)
            {
                _listening = NativeMethods.AddClipboardFormatListener(_window.Handle);
            }
            else
            {
                NativeMethods.RemoveClipboardFormatListener(_window.Handle);
                _listening = false;
                _items.Clear();                      // forget everything when turned off
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetImagesEnabled(bool enabled)
        {
            if (ImagesEnabled == enabled) return;
            ImagesEnabled = enabled;
            if (!enabled && _items.RemoveAll(i => i.IsImage) > 0) Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Puts an item back on the clipboard.</summary>
        public void Copy(ClipItem item)
        {
            try
            {
                if (item.Image != null)
                {
                    _ownImageCopy = true;
                    Clipboard.SetImage(item.Image);
                }
                else if (item.Text != null)
                {
                    _ownCopy = item.Text;
                    Clipboard.SetText(item.Text);
                }
            }
            catch (Exception ex)
            {
                App.LogError("Copy to clipboard failed", ex);
            }
        }

        private void ReadClipboard()
        {
            if (!_listening) return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        ReadText(Clipboard.GetText());
                        return;
                    }
                    if (ImagesEnabled && Clipboard.ContainsImage())
                    {
                        ReadImage(Clipboard.GetImage());
                        return;
                    }
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(30);   // clipboard busy – try again
                }
            }
        }

        private void ReadText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text == _ownCopy) { _ownCopy = null; return; }

            text = text.Trim();
            if (text.Length > MaxLength) text = text[..MaxLength] + "…";

            _items.RemoveAll(i => i.Text == text);
            Insert(new ClipItem { Text = text });
        }

        private void ReadImage(BitmapSource? source)
        {
            if (source == null) return;
            if (_ownImageCopy) { _ownImageCopy = false; return; }
            if (source.PixelWidth <= 0 || source.PixelHeight <= 0 ||
                source.PixelWidth > MaxImageSide || source.PixelHeight > MaxImageSide) return;

            // Own copies of the pixels: the clipboard's bitmap goes away with the next copy.
            var full = new WriteableBitmap(source);
            full.Freeze();

            double scale = Math.Min(1.0, 96.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource thumb = new TransformedBitmap(full, new ScaleTransform(scale, scale));
            thumb.Freeze();

            Insert(new ClipItem { Image = full, Thumbnail = thumb });

            // Images are big: keep only the newest few.
            int images = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (!_items[i].IsImage) continue;
                if (++images > MaxImages) _items.RemoveAt(i--);
            }
        }

        private void Insert(ClipItem item)
        {
            _items.Insert(0, item);
            if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            SetEnabled(false);
            _window.Dispose();
        }
    }
}
