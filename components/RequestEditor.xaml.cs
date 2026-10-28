using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using northApi.models;
using northApi.services;

namespace northApi.components;

public partial class RequestEditor : UserControl, IDisposable
{
    private static readonly HttpRequestEngine Engine = new();
    private readonly ObservableCollection<RequestField> parameters = [new()];
    private readonly ObservableCollection<RequestField> headers = [new()];
    private CancellationTokenSource? activeRequest;
    private string responseCookies = "No cookies in the response.";
    private bool disposed;
    private string requestId = Guid.NewGuid().ToString("N");
    private string? collectionId;
    private string? environmentId;
    private bool updatingEnvironments;
    public event EventHandler<string>? TitleChanged;
    public string RequestId => requestId;

    private readonly WorkspaceStore store;
    public RequestEditor() : this(WorkspaceStore.Shared) { }
    public RequestEditor(WorkspaceStore store)
    {
        this.store = store;
        InitializeComponent();
        ParamsGrid.ItemsSource = parameters; HeadersGrid.ItemsSource = headers;
        RefreshEnvironments();
        store.Changed += Store_Changed;
    }
    public string Title { get => RequestTitle.Text; set => RequestTitle.Text = value; }
    private void Store_Changed(object? sender, EventArgs e) { if (!disposed) RefreshEnvironments(); }
    private void RefreshEnvironments()
    {
        updatingEnvironments = true;
        EnvironmentSelector.Items.Clear();
        EnvironmentSelector.Items.Add(new ComboBoxItem { Content = "No Environment" });
        foreach (var environment in store.Snapshot().Environments)
            EnvironmentSelector.Items.Add(new ComboBoxItem { Content = environment.Name, Tag = environment.Id });
        EnvironmentSelector.SelectedItem = EnvironmentSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag,environmentId)) ?? EnvironmentSelector.Items[0];
        environmentId = (EnvironmentSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        updatingEnvironments = false;
    }
    private void Environment_Changed(object sender, SelectionChangedEventArgs e)
    { if (!updatingEnvironments) environmentId = (EnvironmentSelector.SelectedItem as ComboBoxItem)?.Tag as string; }
    private EnvironmentProfile? CurrentEnvironment => store.Snapshot().Environments.FirstOrDefault(item => item.Id == environmentId);
    private void EditEnvironment_Click(object sender, RoutedEventArgs e) => WorkspaceDialogs.Environment(this,CurrentEnvironment,store);

    public RequestDefinition Snapshot()
    {
        foreach (var grid in new[] { ParamsGrid, HeadersGrid }) { grid.CommitEdit(DataGridEditingUnit.Cell,true); grid.CommitEdit(DataGridEditingUnit.Row,true); }
        if (!int.TryParse(TimeoutBox.Text,out var timeout) || timeout < 1 || timeout > 600) throw new ArgumentException("Timeout must be between 1 and 600 seconds.");
        return new() { Id = requestId, CollectionId = collectionId, EnvironmentId = environmentId, Name = Title, Method = ((ComboBoxItem)MethodSelector.SelectedItem).Content.ToString()!, Url = UrlBox.Text, TimeoutSeconds = timeout, Parameters = parameters.Select(field => field.Copy()).ToList(), Headers = headers.Select(field => field.Copy()).ToList(), Body = RequestBodyEditor.Snapshot(), Auth = Authorization.Snapshot() };
    }
    public void Load(RequestDefinition request)
    {
        requestId = request.Id; collectionId = request.CollectionId; environmentId = request.EnvironmentId;
        Title = request.Name; UrlBox.Text = request.Url; TimeoutBox.Text = request.TimeoutSeconds.ToString();
        MethodSelector.SelectedItem = MethodSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Content,request.Method)) ?? MethodSelector.Items[0];
        parameters.Clear(); foreach (var field in request.Parameters) parameters.Add(RequestField.From(field));
        headers.Clear(); foreach (var field in request.Headers) headers.Add(RequestField.From(field));
        RequestBodyEditor.Load(request.Body); Authorization.Load(request.Auth); RefreshEnvironments();
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (activeRequest is not null) { activeRequest.Cancel(); return; }
        RequestHistory? history = null;
        try
        {
            var definition = Snapshot();
            using var request = RequestBuilder.Build(definition,CurrentEnvironment);
            history = new RequestHistory { Request = definition };
            TryRecordHistory(history);
            using var cancellation = new CancellationTokenSource();
            activeRequest = cancellation; SendButton.Content = "Cancel";
            ResponsePanel.ShowLoading(); responseCookies = "No cookies in the response.";
            var response = await Engine.SendAsync(request,TimeSpan.FromSeconds(definition.TimeoutSeconds),cancellation.Token);
            history.StatusCode = response.StatusCode; history.ElapsedMilliseconds = response.Elapsed.TotalMilliseconds;
            if (disposed) return;
            ResponsePanel.ShowResponse(response);
            if (response.Cookies.Length > 0) responseCookies = string.Join(Environment.NewLine,response.Cookies);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or HttpRequestException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            var message = SafeDiagnostics.RequestError(exception);
            if (history is not null) history.Error = message;
            if (!disposed) ResponsePanel.ShowError(message);
        }
        finally
        {
            if (history is not null) TryRecordHistory(history);
            activeRequest = null;
            if (!disposed) SendButton.Content = "Send";
        }
    }
    private void TryRecordHistory(RequestHistory history)
    {
        try { store.RecordHistory(history); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { PersistenceNotice.Text = "History could not be saved. Check local storage permissions."; PersistenceNotice.Visibility = Visibility.Visible; }
    }
    private void Url_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Send_Click(sender,e); e.Handled = true; } }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var definition = Snapshot();
            var choice = WorkspaceDialogs.SaveRequest(this,definition,store);
            if (choice is null) return;
            definition.Name = choice.Value.Name;
            store.SaveRequest(definition,choice.Value.Collection);
            collectionId = store.Snapshot().Collections.First(collection => collection.Requests.Any(item => item.Id == requestId)).Id;
            Title = definition.Name; TitleChanged?.Invoke(this,Title);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { MessageBox.Show(SafeDiagnostics.LocalError,"Unable to save request"); }
    }
    private void Code_Click(object sender, RoutedEventArgs e)
    {
        try { WorkspaceDialogs.Code(this,CodeGenerator.Generate(Snapshot(),CurrentEnvironment)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException) { MessageBox.Show(SafeDiagnostics.RequestError(exception),"Unable to generate code"); }
    }
    private void Cookies_Click(object sender, RoutedEventArgs e) => MessageBox.Show(responseCookies,"Response cookies");
    public void Dispose()
    { disposed = true; activeRequest?.Cancel(); store.Changed -= Store_Changed; GC.SuppressFinalize(this); }
}

public class RequestField : FieldValue
{
    public FieldValue Copy() => new() { Enabled = Enabled, Key = Key, Value = Value };
    public static RequestField From(FieldValue field) => new() { Enabled = field.Enabled, Key = field.Key, Value = field.Value };
}
