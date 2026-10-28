using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using northApi.models;
using northApi.services;

namespace northApi.components;

public partial class Sidebar : UserControl
{
    public event EventHandler? NewRequestRequested;
    public event EventHandler? ConsoleRequested;
    public event EventHandler<RequestDefinition>? RequestOpenRequested;
    private string activeTab = "Collections";
    private readonly WorkspaceStore store;
    public Sidebar() : this(WorkspaceStore.Shared) { }
    public Sidebar(WorkspaceStore store)
    {
        this.store = store;
        InitializeComponent(); RenderCollections();
        Loaded += (_, _) => { store.Changed -= StoreChanged; store.Changed += StoreChanged; RenderCollections(); };
        Unloaded += (_, _) => store.Changed -= StoreChanged;
    }
    private void StoreChanged(object? sender, EventArgs e) => RenderCollections();
    private void RenderCollections()
    {
        if (CollectionList is null || FilterBox is null) return;
        var expanded = CollectionList.Children.OfType<Expander>().Where(item => item.IsExpanded).Select(item => item.Tag as string).ToHashSet();
        CollectionList.Children.Clear();
        var state = store.Snapshot();
        var filter = FilterBox.Text;
        bool Matches(string value) => value.Contains(filter,StringComparison.OrdinalIgnoreCase);
        if (activeTab == "History")
        {
            foreach (var entry in state.History.Where(entry => Matches(entry.Request.Name) || Matches(entry.Request.Url) || Matches(entry.Request.Method)))
            {
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = $"{entry.Request.Method}  {entry.Request.Name}", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 245 });
                label.Children.Add(new TextBlock { Text = $"{entry.SentAt.UtcDateTime:MMM dd HH:mm} UTC · {entry.StatusCode?.ToString() ?? (entry.Error is null ? "Sending" : "Failed")}", Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0,4,0,0) });
                var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = entry.Request.Url + (entry.Error is null ? "" : "\n" + entry.Error) };
                button.Click += (_, _) => { var copy = WorkspaceStore.Copy(entry.Request); copy.Id = Guid.NewGuid().ToString("N"); copy.CollectionId = null; RequestOpenRequested?.Invoke(this,copy); };
                CollectionList.Children.Add(button);
            }
            EmptyMessage.Text = state.History.Count == 0 ? "Sent requests will appear here." : "No matching requests.";
        }
        else if (activeTab == "Environments")
        {
            var add = new Button { Content = "+ New environment", HorizontalContentAlignment = HorizontalAlignment.Left };
            add.Click += (_, _) => WorkspaceDialogs.Environment(this,workspace: store); CollectionList.Children.Add(add);
            foreach (var environment in state.Environments.Where(item => Matches(item.Name)))
            {
                var button = new Button { Content = environment.Name, HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = "Edit environment variables" };
                button.Click += (_, _) => WorkspaceDialogs.Environment(this,environment,store); CollectionList.Children.Add(button);
            }
            EmptyMessage.Text = state.Environments.Count == 0 ? "Create an environment to use {{variables}}." : "";
        }
        else
        {
            foreach (var collection in state.Collections)
            {
                var matchesCollection = Matches(collection.Name);
                var requests = collection.Requests.Where(item => matchesCollection || Matches(item.Name) || Matches(item.Url)).ToList();
                if (!matchesCollection && requests.Count == 0) continue;
                var content = new StackPanel();
                foreach (var request in requests)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(new TextBlock { Text = request.Method, Width = 52, FontSize = 10, Foreground = MethodBrush(request.Method), VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(new TextBlock { Text = request.Name, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis });
                    var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = request.Url };
                    button.Click += (_, _) => RequestOpenRequested?.Invoke(this,WorkspaceStore.Copy(request)); content.Children.Add(button);
                }
                var add = new Button { Content = "+ New request", HorizontalContentAlignment = HorizontalAlignment.Left };
                add.Click += (_, _) => RequestOpenRequested?.Invoke(this,new RequestDefinition { CollectionId = collection.Id }); content.Children.Add(add);
                CollectionList.Children.Add(new Expander { Header = collection.Name, Tag = collection.Id, Content = content, IsExpanded = expanded.Contains(collection.Id) || filter.Length > 0 });
            }
            EmptyMessage.Text = state.Collections.Count == 0 ? "No collections yet. Click + to create one." : "No matching collections.";
        }
        EmptyMessage.Visibility = activeTab == "Environments" ? (state.Environments.Count == 0 ? Visibility.Visible : Visibility.Collapsed) : (CollectionList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed);
    }
    private static Brush MethodBrush(string method) => (Brush)new BrushConverter().ConvertFromString(method switch { "GET" => "#73DDA3", "POST" => "#F2C66D", "PUT" => "#75B9F5", "PATCH" => "#BD9CF5", "DELETE" => "#F48787", _ => "#66D4D4" })!;
    private void Tab_Checked(object sender, RoutedEventArgs e) { activeTab = (sender as RadioButton)?.Tag as string ?? "Collections"; RenderCollections(); }
    private void Filter_Changed(object sender, TextChangedEventArgs e) => RenderCollections();
    private void NewRequest_Click(object sender, RoutedEventArgs e) => NewRequestRequested?.Invoke(this,EventArgs.Empty);
    private void Console_Click(object sender, RoutedEventArgs e) => ConsoleRequested?.Invoke(this,EventArgs.Empty);
    private void AddCollection_Click(object sender, RoutedEventArgs e)
    {
        if (activeTab == "Environments") { WorkspaceDialogs.Environment(this,workspace: store); return; }
        var name = WorkspaceDialogs.Prompt(this,"New collection","Collection name");
        if (name is null) return;
        try { store.AddCollection(name); CollectionsTab.IsChecked = true; FilterBox.Clear(); RenderCollections(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { MessageBox.Show(northApi.services.SafeDiagnostics.LocalError,"Unable to create collection"); }
    }
    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        var name = WorkspaceDialogs.Prompt(this,"New workspace","Workspace name");
        if (name is null) return;
        WorkspaceSelector.Items.Add(new ComboBoxItem { Content = name }); WorkspaceSelector.SelectedIndex = WorkspaceSelector.Items.Count - 1;
    }
    private void RequestMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu(); var item = new MenuItem { Header = "HTTP Request" }; item.Click += NewRequest_Click; menu.Items.Add(item); menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var add = new MenuItem { Header = "New collection" }; add.Click += (_, _) => { CollectionsTab.IsChecked = true; AddCollection_Click(sender,e); }; menu.Items.Add(add);
        var environment = new MenuItem { Header = "New environment" }; environment.Click += (_, _) => WorkspaceDialogs.Environment(this,workspace: store); menu.Items.Add(environment);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "NorthApi collections (*.json)|*.json", Title = "Import collections and saved requests" };
        if (dialog.ShowDialog() != true) return;
        try { store.ImportCollections(File.ReadAllText(dialog.FileName)); CollectionsTab.IsChecked = true; RenderCollections(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { MessageBox.Show(northApi.services.SafeDiagnostics.LocalError,"Unable to import collections"); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "NorthApi collections (*.json)|*.json", FileName = "northApi-collections.json" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName,store.ExportCollections()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { MessageBox.Show(northApi.services.SafeDiagnostics.LocalError,"Unable to export collections"); }
    }
}
