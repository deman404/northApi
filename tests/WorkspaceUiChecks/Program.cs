using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using northApi.components;
using northApi.models;
using northApi.services;

class Program
{
    private static int passed;
    [STAThread]
    static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var path = Path.Combine(Path.GetTempPath(),"NorthApiUiChecks",Guid.NewGuid().ToString("N"),"workspace.json");
        var store = new WorkspaceStore(path);
        using var listener = new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Respond(listener);
        var environment = new EnvironmentProfile { Name = "Test Local", Variables = [new() { Key = "baseUrl", Value = $"http://127.0.0.1:{port}" },new() {Key="token",Value="test-token"}] };
        store.SaveEnvironment(environment);
        var source = new RequestDefinition
        {
            Name = "Saved JSON request", Url = "{{baseUrl}}/echo", Method = "POST", EnvironmentId = environment.Id,
            Auth = new() {Type="bearer",Token="{{token}}"}, Body = new() {Mode="raw",Raw="{\"name\":\"NorthApi\"}",ContentType="application/json"}, Headers=[new(){Key="X-Check",Value="editor"}]
        };
        var editor = new RequestEditor(store);
        editor.Load(source);
        var snapshot = editor.Snapshot();
        Check(snapshot.Url==source.Url && snapshot.EnvironmentId==environment.Id && snapshot.Auth.Token=="{{token}}" && snapshot.Body.Raw==source.Body.Raw,"editor restores URL, environment, body and Bearer auth");
        Check(((ComboBox)editor.FindName("EnvironmentSelector")).SelectedItem is ComboBoxItem item && Equals(item.Tag,environment.Id),"saved environment selected in toolbar");
        ((Button)editor.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Equals(((Button)editor.FindName("SendButton")).Content,"Send") && store.Snapshot().History.Count>0 && store.Snapshot().History[0].StatusCode is not null);
        var response = (ResponseViewer)editor.FindName("ResponsePanel");
        Check(((TextBlock)response.FindName("StatusText")).Text=="200 OK","Send button displays real HTTP response status");
        var observed = server.GetAwaiter().GetResult();
        Check(observed.Contains("Bearer test-token") && observed.Contains("NorthApi") && observed.Contains("POST /echo"),"editor sends resolved auth and JSON body to HTTP server");
        Check(store.Snapshot().History.Single().Request.Url=="{{baseUrl}}/echo","history records reusable template rather than replacing it");
        RunDialog(() => WorkspaceDialogs.Environment(editor,workspace:store),"New environment",dialog =>
        {
            var inputs=WorkspaceDialogs.Descendants(dialog).OfType<TextBox>().ToArray();inputs[0].Text="Dialog Environment";
            var grid=WorkspaceDialogs.Descendants(dialog).OfType<DataGrid>().Single();
            ((System.Collections.ObjectModel.ObservableCollection<FieldValue>)grid.ItemsSource).Add(new(){Key="baseUrl",Value="http://localhost"});
            WorkspaceDialogs.Descendants(dialog).OfType<Button>().Single(button=>Equals(button.Content,"Save environment")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Check(store.Snapshot().Environments.Any(item=>item.Name=="Dialog Environment" && item.Variables.Single().Key=="baseUrl"),"environment dialog saves local variables");
        RunDialog(() => WorkspaceDialogs.Descendants(editor).OfType<Button>().First(button=>button.ToolTip is string tip && tip=="Save request to collection").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),"Save request",dialog=>
        {
            var inputs=WorkspaceDialogs.Descendants(dialog).OfType<TextBox>().ToArray();inputs[0].Text="Saved JSON request";inputs[1].Text="Local APIs";
            WorkspaceDialogs.Descendants(dialog).OfType<Button>().Single(button=>Equals(button.Content,"Save to collection")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Check(store.Snapshot().Collections.Single().Requests.Single().Id==source.Id,"Save button persists a collection request through the dialog");
        var restarted = new WorkspaceStore(path);
        var saved = restarted.Snapshot().Collections.Single().Requests.Single();
        var reopened = new RequestEditor(restarted); reopened.Load(saved);
        Check(reopened.Snapshot().Body.Raw==source.Body.Raw && reopened.Snapshot().Auth.Token==source.Auth.Token,"saved request reopens after restarting local store");
        var basic = WorkspaceStore.Copy(saved); basic.Auth = new(){Type="basic",Username="user",Password="pass"}; reopened.Load(basic);
        Check(reopened.Snapshot().Auth.Username=="user" && reopened.Snapshot().Auth.Password=="pass","Basic auth editor round trip");
        var sidebar = new Sidebar(restarted);
        RequestDefinition? opened = null; sidebar.RequestOpenRequested += (_,request) => opened=request;
        var collectionList = (StackPanel)sidebar.FindName("CollectionList");
        var collection = (Expander)collectionList.Children[0];
        ((Button)((StackPanel)collection.Content).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(opened?.Id==saved.Id,"sidebar opens saved collection request");
        var historyRadio = WorkspaceDialogs.Descendants(sidebar).OfType<RadioButton>().Single(radio => Equals(radio.Tag,"History"));
        historyRadio.IsChecked=true;
        ((Button)collectionList.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(opened is not null && opened.Id!=saved.Id && opened.CollectionId is null && opened.Body.Raw==source.Body.Raw,"sidebar history reopens an independent request copy");
        var envRadio = WorkspaceDialogs.Descendants(sidebar).OfType<RadioButton>().Single(radio => Equals(radio.Tag,"Environments"));
        envRadio.IsChecked=true;
        Check(collectionList.Children.Count==3,"environment sidebar shows create action and saved environments");
        var body = (BodyEditor)reopened.FindName("RequestBodyEditor");
        var graph = new BodyDefinition {Mode="graphql",Query="query { item { id } }",Variables="{\"id\":1}"}; body.Load(graph);
        Check(body.Snapshot().Mode=="graphql" && body.Snapshot().Variables==graph.Variables,"body mode and GraphQL fields restore");
        var code=CodeGenerator.Generate(saved,environment);
        RunDialog(()=>WorkspaceDialogs.Code(reopened,code),"Generate code",dialog=>
        {
            var selector=WorkspaceDialogs.Descendants(dialog).OfType<ComboBox>().Single();
            var text=WorkspaceDialogs.Descendants(dialog).OfType<TextBox>().Single();
            Check(text.Text.Contains("HttpClient"),"code dialog displays C# snippet");
            selector.SelectedIndex=1;
            Check(text.Text.StartsWith("curl "),"code dialog switches to cURL snippet");dialog.Close();
        });
        Check(code.CSharp.Contains("HttpClient") && code.Curl.Contains("--data-raw"),"code generator uses reopened editor definition");
        reopened.Dispose();editor.Dispose();
        Console.WriteLine($"{passed} WPF workflow checks passed.");
    }
    private static void RunDialog(Action trigger,string title,Action<Window> interact)
    {
        Exception? failure=null;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
        timer.Tick+=(_,_)=>
        {
            var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(window=>window.Title==title);
            if(dialog is null)return;
            timer.Stop();
            try{interact(dialog);}catch(Exception exception){failure=exception;dialog.Close();}
        };
        timer.Start();trigger();timer.Stop();if(failure is not null)throw failure;
    }
    private static void PumpUntil(Func<bool> complete)
    {
        var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(10);
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
        timer.Tick+=(_,_)=>{if(complete() || DateTime.UtcNow>deadline)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        if(!complete())throw new Exception("Timed out waiting for WPF request completion.");
    }
    private static async Task<string> Respond(TcpListener listener)
    {
        using var client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        var stream=client.GetStream();using var buffer=new MemoryStream();var one=new byte[1];var ending=0;
        while(await stream.ReadAsync(one).ConfigureAwait(false)>0){buffer.WriteByte(one[0]);ending=(ending<<8)|one[0];if(ending==0x0D0A0D0A)break;}
        var headers=Encoding.ASCII.GetString(buffer.ToArray());
        var lengthLine=headers.Split("\r\n").First(line=>line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase));
        var body=new byte[int.Parse(lengthLine.Split(':')[1].Trim())];await stream.ReadExactlyAsync(body).ConfigureAwait(false);
        var payload=Encoding.UTF8.GetBytes("{\"ok\":true}");
        var head=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);await stream.WriteAsync(payload).ConfigureAwait(false);
        return headers+Encoding.UTF8.GetString(body);
    }
    private static void Check(bool result,string message){if(!result)throw new Exception(message);Console.WriteLine("PASS: "+message);passed++;}
}
