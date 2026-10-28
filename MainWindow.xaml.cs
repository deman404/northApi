using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace northApi;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = $"v{version?.ToString(3) ?? "1.0.0"}";
        Loaded += ShowWorkspace;
    }

    private async void ShowWorkspace(object sender, RoutedEventArgs e)
    {
        await System.Threading.Tasks.Task.Delay(1600);
        if (!IsVisible) return;
        var workspace = new pages.WorkspaceWindow();
        Application.Current.MainWindow = workspace;
        workspace.Show();
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
