using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoMarkerReID.Windows;

namespace AutoMarkerReID.App;

public static class DarkMessageBox
{
    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MinHeight = 170,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["WindowBackgroundBrush"],
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextBrush"],
            Owner = owner is { IsVisible: true } ? owner : null,
        };
        WindowsDarkMode.Apply(dialog);

        var result = defaultResult == MessageBoxResult.None
            ? buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK
            : defaultResult;
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new TextBlock
        {
            Text = IconFor(image),
            FontSize = 30,
            Foreground = image is MessageBoxImage.Warning or MessageBoxImage.Error ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.DeepSkyBlue,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 21,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        root.Children.Add(icon);
        root.Children.Add(text);

        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 2);
        void AddButton(string caption, MessageBoxResult value, bool isDefault, bool isCancel)
        {
            var button = new System.Windows.Controls.Button { Content = caption, MinWidth = 90, IsDefault = isDefault, IsCancel = isCancel };
            button.Click += (_, _) => { result = value; dialog.DialogResult = true; };
            actions.Children.Add(button);
        }

        if (buttons == MessageBoxButton.YesNo)
        {
            AddButton("Không", MessageBoxResult.No, defaultResult == MessageBoxResult.No, true);
            AddButton("Có", MessageBoxResult.Yes, defaultResult == MessageBoxResult.Yes, false);
        }
        else
        {
            AddButton("OK", MessageBoxResult.OK, true, true);
        }
        root.Children.Add(actions);
        dialog.Content = root;
        dialog.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape) dialog.Close();
        };
        dialog.ShowDialog();
        return result;
    }

    private static string IconFor(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error => "✕",
        MessageBoxImage.Warning => "⚠",
        MessageBoxImage.Question => "?",
        MessageBoxImage.Information => "ⓘ",
        _ => string.Empty,
    };
}
