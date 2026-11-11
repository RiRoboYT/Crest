using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Crest.Models;

namespace Crest
{
    /// <summary>Settings window, v1.5: search, Simple view and the page order editor.</summary>
    public partial class SettingsWindow
    {
        private sealed record SettingRow(string Page, string Card, string Title, string Description, FrameworkElement Element);

        private readonly List<SettingRow> _index = new();
        private bool _syncingOrder;

        /// <summary>Pages of the open notch, in default order, with the setting that switches each on.</summary>
        private static readonly (string Name, string Label, string? Toggle)[] NotchPages =
        {
            ("Music", "Music", null), ("Lyrics", "Lyrics", nameof(AppSettings.PageLyrics)), ("Calendar", "Calendar", nameof(AppSettings.PageCalendar)),
            ("Weather", "Weather", nameof(AppSettings.PageWeather)), ("System", "System", nameof(AppSettings.PageSystem)),
            ("Timer", "Timer & focus", nameof(AppSettings.PageTimer)), ("Shelf", "File shelf", nameof(AppSettings.PageShelf)),
            ("Notes", "Notes & to-do", nameof(AppSettings.PageNotes)), ("Sound", "Sound", nameof(AppSettings.PageSound)),
            ("Controls", "Controls", nameof(AppSettings.PageControls)), ("Tools", "Tools", nameof(AppSettings.PageTools)),
            ("Recent", "Recent", nameof(AppSettings.PageRecent)),
        };

        /// <summary>Cards that stay visible in Simple view (by their heading).</summary>
        private static readonly HashSet<string> SimpleCards = new(StringComparer.OrdinalIgnoreCase)
        {
            "Presets", "Colors", "Effects", "Shape", "Size", "Now playing", "Pop-ups", "Pages in the open notch",
            "Weather", "General", "Hover & animation", "Microphone"
        };

        private void InitV15()
        {
            BuildSearchIndex();
            BuildTabOrderList();
            BuildStartPageCombo();
            ApplySimpleView();
        }

        private void OnSettingsChangedV15(string? name)
        {
            switch (name)
            {
                case nameof(AppSettings.SimpleSettings):
                    ApplySimpleView();
                    break;
                case nameof(AppSettings.TabOrder):
                    BuildTabOrderList();
                    break;
                case nameof(AppSettings.StartPage):
                    BuildStartPageCombo();
                    break;
            }
            if (name != null && name.StartsWith("Page", StringComparison.Ordinal)) BuildTabOrderList();
        }

        private IEnumerable<(string Tag, StackPanel Page)> SettingsPages() => new[]
        {
            ("Appearance", PageAppearance), ("Size", PageSize), ("Spectrum", PageSpectrum), ("Media", PageMedia),
            ("Activities", PageActivities), ("Widgets", PageWidgets), ("Behavior", PageBehavior), ("About", PageAbout)
        };

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        private static string CardTitle(Border card)
        {
            var h2Style = Application.Current.TryFindResource("H2") as Style;
            foreach (var tb in Descendants(card).OfType<TextBlock>())
                if (tb.Style == h2Style || tb.Style == (card.TryFindResource("H2") as Style)) return tb.Text;
            return "";
        }

        // =====================================================================
        //  Search
        // =====================================================================

        private void BuildSearchIndex()
        {
            _index.Clear();
            var rowTitle = TryFindResource("RowTitle") as Style;
            var rowDesc = TryFindResource("RowDesc") as Style;
            var cardStyle = TryFindResource("Card") as Style;
            foreach (var (tag, page) in SettingsPages())
            {
                foreach (var card in Descendants(page).OfType<Border>().Where(b => b.Style == cardStyle))
                {
                    string cardTitle = CardTitle(card);
                    foreach (var title in Descendants(card).OfType<TextBlock>().Where(t => t.Style == rowTitle && t.Text.Length > 0))
                    {
                        // The row is the nearest DockPanel / StackPanel row around the title.
                        FrameworkElement row = title;
                        var parent = LogicalTreeHelper.GetParent(title) as FrameworkElement;
                        while (parent != null && parent != card && parent is not DockPanel)
                            parent = LogicalTreeHelper.GetParent(parent) as FrameworkElement;
                        if (parent is DockPanel) row = parent;

                        string desc = Descendants(row).OfType<TextBlock>().FirstOrDefault(t => t.Style == rowDesc)?.Text ?? "";
                        if (_index.Any(r => r.Element == row && r.Title == title.Text)) continue;
                        _index.Add(new SettingRow(tag, cardTitle, title.Text, desc, row));
                    }
                }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = SearchBox.Text.Trim();
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (q.Length == 0)
            {
                SearchResults.Visibility = Visibility.Collapsed;
                PagesHost.Visibility = Visibility.Visible;
                Nav_SelectionChanged(Nav, null!);
                return;
            }

            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits = _index
                .Select(r => (r, score: words.All(w => (r.Title + " " + r.Description + " " + r.Card).Contains(w, StringComparison.OrdinalIgnoreCase))
                    ? (r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 1) : 0))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(25)
                .Select(x => x.r)
                .ToList();

            PageTitle.Text = "Search";
            PageSubtitle.Text = hits.Count == 0 ? $"Nothing found for “{q}”." : $"{hits.Count} setting{(hits.Count == 1 ? "" : "s")} – click one to go there.";
            PagesHost.Visibility = Visibility.Collapsed;
            PreviewCard.Visibility = Visibility.Collapsed;
            SearchResults.Visibility = Visibility.Visible;
            SearchResults.Children.Clear();

            foreach (var hit in hits)
            {
                var title = new TextBlock { Text = hit.Title, Style = (Style)FindResource("RowTitle") };
                var where = new TextBlock
                {
                    Text = Pages.FirstOrDefault(p => p.Tag == hit.Page).Title + (hit.Card.Length > 0 ? "  ›  " + hit.Card : ""),
                    Style = (Style)FindResource("RowDesc")
                };
                var stack = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
                stack.Children.Add(title);
                stack.Children.Add(where);
                if (hit.Description.Length > 0)
                    stack.Children.Add(new TextBlock { Text = hit.Description, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

                var button = new Button
                {
                    Style = (Style)FindResource("GhostButton"),
                    Content = stack,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var target = hit;
                button.Click += (_, _) => GoTo(target);
                SearchResults.Children.Add(button);
            }
        }

        /// <summary>Opens the page of a search hit, scrolls to the row and makes it glow for a moment.</summary>
        private void GoTo(SettingRow row)
        {
            SearchBox.Clear();
            // In Simple view the row's card may be hidden: show everything then.
            if (SimpleView)
            {
                DependencyObject? p = row.Element;
                while (p != null && !(p is Border b && b.Style == TryFindResource("Card") as Style)) p = LogicalTreeHelper.GetParent(p);
                if (p is Border card && card.Visibility != Visibility.Visible) _settings.SimpleSettings = false;
            }

            var item = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == row.Page);
            if (item != null) Nav.SelectedItem = item;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                row.Element.BringIntoView();
                var flash = new SolidColorBrush(Color.FromArgb(0, 0x7B, 0x6C, 0xFF));
                var old = (row.Element as Panel)?.Background;
                if (row.Element is Panel panel)
                {
                    panel.Background = flash;
                    var anim = new ColorAnimation(Color.FromArgb(0x55, 0x7B, 0x6C, 0xFF), TimeSpan.FromMilliseconds(350))
                    {
                        AutoReverse = true,
                        RepeatBehavior = new RepeatBehavior(2)
                    };
                    anim.Completed += (_, _) => panel.Background = old;
                    flash.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }
            }), DispatcherPriority.Loaded);
        }

        // =====================================================================
        //  Simple view
        // =====================================================================

        private bool SimpleView => _settings.SimpleSettings;

        private void ApplySimpleView()
        {
            var cardStyle = TryFindResource("Card") as Style;
            foreach (var (tag, page) in SettingsPages())
            {
                if (tag == "About") continue;
                foreach (var card in page.Children.OfType<Border>().Where(b => b.Style == cardStyle))
                    card.Visibility = !SimpleView || SimpleCards.Contains(CardTitle(card)) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // =====================================================================
        //  Page order + start page
        // =====================================================================

        private List<string> CurrentOrder()
        {
            var saved = _settings.TabOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var all = NotchPages.Select(p => p.Name).ToList();
            return saved.Where(all.Contains).Concat(all.Where(n => !saved.Contains(n))).ToList();
        }

        private bool PageOn(string? toggle)
            => toggle == null || typeof(AppSettings).GetProperty(toggle)?.GetValue(_settings) is true;

        private void BuildTabOrderList()
        {
            if (TabOrderList == null) return;
            TabOrderList.Children.Clear();
            var order = CurrentOrder();
            for (int i = 0; i < order.Count; i++)
            {
                var info = NotchPages.First(p => p.Name == order[i]);
                bool on = PageOn(info.Toggle);

                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var down = new Button { Style = (Style)FindResource("GhostButton"), Content = "↓", Width = 34, Padding = new Thickness(0, 4, 0, 4), IsEnabled = i < order.Count - 1, ToolTip = "Move down" };
                var up = new Button { Style = (Style)FindResource("GhostButton"), Content = "↑", Width = 34, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(0, 0, 6, 0), IsEnabled = i > 0, ToolTip = "Move up" };
                int index = i;
                up.Click += (_, _) => MovePage(index, -1);
                down.Click += (_, _) => MovePage(index, 1);
                DockPanel.SetDock(down, Dock.Right);
                DockPanel.SetDock(up, Dock.Right);
                row.Children.Add(down);
                row.Children.Add(up);

                var label = new TextBlock
                {
                    Text = $"{i + 1}.  {info.Label}" + (on ? "" : "   (off)"),
                    Style = (Style)FindResource("RowTitle"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = on ? 1 : 0.5
                };
                row.Children.Add(label);
                TabOrderList.Children.Add(row);
            }
        }

        private void MovePage(int index, int delta)
        {
            var order = CurrentOrder();
            int to = index + delta;
            if (to < 0 || to >= order.Count) return;
            (order[index], order[to]) = (order[to], order[index]);
            _settings.TabOrder = string.Join(",", order);
        }

        private void BuildStartPageCombo()
        {
            if (StartPageCombo == null) return;
            _syncingOrder = true;
            try
            {
                var items = new List<KeyValuePair<string, string>> { new("Last", "Where I left off") };
                items.AddRange(NotchPages.Select(p => new KeyValuePair<string, string>(p.Name, p.Label)));
                StartPageCombo.ItemsSource = items;
                StartPageCombo.DisplayMemberPath = "Value";
                StartPageCombo.SelectedValuePath = "Key";
                StartPageCombo.SelectedValue = _settings.StartPage;
                if (StartPageCombo.SelectedIndex < 0) StartPageCombo.SelectedIndex = 0;
            }
            finally
            {
                _syncingOrder = false;
            }
        }

        private void StartPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingOrder || StartPageCombo.SelectedValue is not string page) return;
            _settings.StartPage = page;
        }
    }
}
