using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Crest.Helpers;
using Crest.Models;
using Crest.Services;
using Forms = System.Windows.Forms;

namespace Crest
{
    /// <summary>
    /// Settings UI. Bound directly to the live <see cref="AppSettings"/> instance, so every
    /// change is applied to the notch immediately and auto-saved by <see cref="SettingsManager"/>.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsManager _manager;

        public SettingsWindow(SettingsManager manager)
        {
            _manager = manager;
            InitializeComponent();

            DataContext = manager.Settings;
            ShapeCombo.ItemsSource = Enum.GetValues<NotchShape>();

            // Font list is only materialized while this window is open.
            FontCombo.ItemsSource = Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SettingsPathRun.Text = manager.SettingsPath;

            // Enter in a text box commits it immediately (hex colors, font name).
            PreviewKeyDown += OnPreviewKeyDown;
            Closed += (_, _) => Notch?.SetPreviewExpanded(false);
        }

        private static MainWindow? Notch => Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (Keyboard.FocusedElement is TextBox tb)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
            else if (Keyboard.FocusedElement is DependencyObject d && FindParent<ComboBox>(d) is ComboBox cb)
            {
                cb.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }

        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string propertyName }) return;

            PropertyInfo? prop = typeof(AppSettings).GetProperty(propertyName);
            if (prop == null || prop.PropertyType != typeof(string)) return;

            var current = ColorHelper.Parse(prop.GetValue(_manager.Settings) as string, Colors.White);

            using var dialog = new Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                var c = dialog.Color;
                prop.SetValue(_manager.Settings, ColorHelper.ToHex(Color.FromRgb(c.R, c.G, c.B)));
            }
        }

        private void PreviewCheck_Changed(object sender, RoutedEventArgs e)
            => Notch?.SetPreviewExpanded(PreviewCheck.IsChecked == true);

        private void CenterOffset_Click(object sender, RoutedEventArgs e)
            => _manager.Settings.HorizontalOffset = 0;

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                "Reset all Crest settings to their defaults?",
                "Crest", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                _manager.Settings.CopyFrom(new AppSettings());
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void SettingsPathLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _manager.Save();
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_manager.SettingsPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError("Opening settings folder failed", ex);
            }
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? current = child;
            while (current != null)
            {
                if (current is T match) return match;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
