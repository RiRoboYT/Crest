using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>
    /// Optional clipboard history for the Notes page: the last few copied texts.
    /// Kept in memory only – never written to disk – and only while enabled.
    /// </summary>
    public sealed class ClipboardService : IDisposable
    {
        private const int MaxItems = 8;
        private const int MaxLength = 400;

        private readonly MessageWindow _window = new("CrestClipboard");
        private readonly Dispatcher _dispatcher;
        private readonly List<string> _items = new();
        private bool _listening;
        private string? _ownCopy;

        public event EventHandler? Changed;

        public IReadOnlyList<string> Items => _items;

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

        /// <summary>Puts an item back on the clipboard.</summary>
        public void Copy(string text)
        {
            try
            {
                _ownCopy = text;
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                App.LogError("Copy to clipboard failed", ex);
            }
        }

        private void ReadClipboard()
        {
            if (!_listening) return;
            string? text = null;
            for (int attempt = 0; attempt < 3 && text == null; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) return;
                    text = Clipboard.GetText();
                }
                catch
                {
                    System.Threading.Thread.Sleep(30);   // clipboard busy – try again
                }
            }
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text == _ownCopy) { _ownCopy = null; return; }

            text = text.Trim();
            if (text.Length > MaxLength) text = text[..MaxLength] + "…";

            _items.Remove(text);
            _items.Insert(0, text);
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
