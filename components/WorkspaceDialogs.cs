using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using northApi.models;
using northApi.services;

namespace northApi.components;

public static class WorkspaceDialogs
{
    public static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static Window Create(DependencyObject owner, string title, double width, double height)
    {
        var window = new Window { Owner = Window.GetWindow(owner), Title = title, Width = width, Height = height, MinWidth = Math.Min(width, 340), MinHeight = Math.Min(height, 180), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(24,25,25)), Foreground = Brushes.LightGray, FontFamily = new FontFamily("Segoe UI"), FontSize = 12 };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/northApi;component/assets/RequestTheme.xaml", UriKind.Relative) });
        window.SourceInitialized += (_, _) => WindowTheme.UseDarkTitleBar(window);
        return window;
    }
    public static string? Prompt(DependencyObject owner, string title, string label, string initial = "")
    {
        var window = Create(owner, title, 380, 200);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,0,0,9) });
        var input = new TextBox { Text = initial }; panel.Children.Add(input);
        var save = new Button { Content = "Save", IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(38,127,157)), Margin = new Thickness(0,16,0,0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; };
        panel.Children.Add(save); window.Content = panel; window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public static (string Name, string Collection)? SaveRequest(DependencyObject owner, RequestDefinition request, WorkspaceStore? workspace = null)
    {
        var state = (workspace ?? WorkspaceStore.Shared).Snapshot();
        var collectionName = state.Collections.FirstOrDefault(item => item.Id == request.CollectionId)?.Name ?? state.Collections.FirstOrDefault()?.Name ?? "My Collection";
        var window = Create(owner, "Save request", 410, 295);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Request name", Margin = new Thickness(0,0,0,8) });
        var name = new TextBox { Text = request.Name }; panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Collection", Margin = new Thickness(0,16,0,8) });
        var collection = new TextBox { Text = collectionName, ToolTip = "Enter an existing collection name or create a new one" }; panel.Children.Add(collection);
        var save = new Button { Content = "Save to collection", IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(38,127,157)), Margin = new Thickness(0,18,0,0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(collection.Text)) window.DialogResult = true; };
        panel.Children.Add(save); window.Content = panel;
        return window.ShowDialog() == true ? (name.Text.Trim(), collection.Text.Trim()) : null;
    }
    public static void Environment(DependencyObject owner, EnvironmentProfile? existing = null, WorkspaceStore? workspace = null)
    {
        var profile = existing is null ? new EnvironmentProfile() : WorkspaceStore.Copy(existing);
        var window = Create(owner, existing is null ? "New environment" : "Edit environment", 660, 450);
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBox { Text = profile.Name, ToolTip = "Environment name", Margin = new Thickness(0,0,0,15) };
        System.Windows.Automation.AutomationProperties.SetName(name, "Environment name"); root.Children.Add(name);
        var fields = new ObservableCollection<FieldValue>(profile.Variables);
        var grid = new DataGrid { ItemsSource = fields, VerticalAlignment = VerticalAlignment.Stretch };
        grid.Columns.Add(new DataGridCheckBoxColumn { Binding = new System.Windows.Data.Binding("Enabled"), Width = new DataGridLength(35) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Variable", Binding = new System.Windows.Data.Binding("Key"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new System.Windows.Data.Binding("Value"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        System.Windows.Automation.AutomationProperties.SetName(grid, "Environment variables"); Grid.SetRow(grid,1); root.Children.Add(grid);
        var footer = new DockPanel { Margin = new Thickness(0,15,0,0) };
        var save = new Button { Content = "Save environment", IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(38,127,157)) }; DockPanel.SetDock(save,Dock.Right); footer.Children.Add(save);
        footer.Children.Add(new TextBlock { Text = "Use variables as {{baseUrl}} in your requests.", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        save.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell,true); grid.CommitEdit(DataGridEditingUnit.Row,true);
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            try
            {
                profile.Name = name.Text.Trim(); profile.Variables = fields.Where(field => !string.IsNullOrWhiteSpace(field.Key)).ToList();
                (workspace ?? WorkspaceStore.Shared).SaveEnvironment(profile); window.DialogResult = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { MessageBox.Show(northApi.services.SafeDiagnostics.LocalError,"Unable to save environment"); }
        };
        Grid.SetRow(footer,2); root.Children.Add(footer); window.Content = root; window.ShowDialog();
    }
    public static void Code(DependencyObject owner, GeneratedCode code)
    {
        var window = Create(owner,"Generate code",850,600);
        var root = new Grid { Margin = new Thickness(16) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        var bar = new DockPanel { Margin = new Thickness(0,0,0,12) };
        var copy = new Button { Content = "Copy", HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(copy,Dock.Right); bar.Children.Add(copy);
        var selector = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left }; selector.Items.Add(new ComboBoxItem { Content = "C# · HttpClient" }); selector.Items.Add(new ComboBoxItem { Content = "cURL · POSIX shell" }); bar.Children.Add(selector);
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(text, "Generated code");
        selector.SelectionChanged += (_, _) => text.Text = selector.SelectedIndex == 0 ? code.CSharp : code.Curl;
        selector.SelectedIndex = 0;
        copy.Click += (_, _) => { try { Clipboard.SetText(text.Text); } catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show("Clipboard is busy. Please try again."); } };
        root.Children.Add(bar); Grid.SetRow(text,1); root.Children.Add(text); window.Content = root; window.ShowDialog();
    }
}
