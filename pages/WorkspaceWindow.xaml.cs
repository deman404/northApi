using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using northApi.components;

namespace northApi.pages;

public partial class WorkspaceWindow : Window
{
    public WorkspaceWindow()
    {
        InitializeComponent();
        NavigationSidebar.RequestOpenRequested += (_, request) => OpenRequest(request);
        if (services.WorkspaceStore.Shared.LoadWarning is string warning) Loaded += (_, _) => MessageBox.Show(warning, "Local workspace");
        SourceInitialized += (_, _) => services.WindowTheme.UseDarkTitleBar(this);
        Closed += (_, _) =>
        {
            foreach (TabItem tab in RequestTabs.Items)
                (tab.Content as RequestEditor)?.Dispose();
        };
    }

    private int requestNumber;

    private void NewRequest_Requested(object sender, EventArgs e)
    {
        OpenRequest(new northApi.models.RequestDefinition { Name = $"Untitled Request {++requestNumber}" });
    }
    private void OpenRequest(northApi.models.RequestDefinition definition)
    {
        foreach (TabItem existing in RequestTabs.Items)
            if (existing.Content is RequestEditor current && current.RequestId == definition.Id) { RequestTabs.SelectedItem = existing; return; }
        var title = definition.Name;
        var editor = new RequestEditor();
        editor.Load(definition);
        var tab = new TabItem { Content = editor, Tag = title };
        System.Windows.Automation.AutomationProperties.SetName(tab, title);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = "HTTP", FontSize = 8, Foreground = Brushes.MediumAquamarine, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        var titleLabel = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(titleLabel);
        editor.TitleChanged += (_, name) => { titleLabel.Text = name; tab.Tag = name; System.Windows.Automation.AutomationProperties.SetName(tab, name); };
        var close = new Button
        {
            Content = "×", ToolTip = "Close request", Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = Brushes.LightGray,
            Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(10, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        System.Windows.Automation.AutomationProperties.SetName(close, $"Close {title}");
        close.Click += (_, _) => { editor.Dispose(); RequestTabs.Items.Remove(tab); };
        header.Children.Add(close);
        tab.Header = header;
        RequestTabs.Items.Add(tab);
        RequestTabs.SelectedItem = tab;
    }

    private void Console_Requested(object sender, EventArgs e) => ConsolePanel.Visibility = ConsolePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
