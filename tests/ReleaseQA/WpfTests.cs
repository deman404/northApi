using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using northApi.components;
using northApi.models;
using northApi.services;
using Xunit;

namespace ReleaseQA;
internal static class WpfThread
{
    private static readonly Dispatcher Dispatcher;
    static WpfThread()
    {
        Dispatcher? dispatcher=null;using var ready=new ManualResetEventSlim();
        var thread=new Thread(()=>{var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));ready.Set();System.Windows.Threading.Dispatcher.Run();});thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();ready.Wait();Dispatcher=dispatcher!;
    }
    public static Task Run(Func<Task> action)=>Dispatcher.InvokeAsync(action).Task.Unwrap();
}
public class WpfTests
{
    [Theory][InlineData(200)][InlineData(400)][InlineData(401)][InlineData(404)][InlineData(429)][InlineData(500)][InlineData(503)]
    public Task Response_viewer_displays_HTTP_errors_and_metadata(int status)=>WpfThread.Run(async()=>
    {
        await using var server=new QaServer();using var request=new HttpRequestMessage(HttpMethod.Get,server.Url+"/status/"+status);
        var result=await new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5));
        var viewer=new ResponseViewer();viewer.ShowResponse(result);
        Assert.StartsWith(status.ToString(),((TextBlock)viewer.FindName("StatusText")).Text);
        var document=((RichTextBox)viewer.FindName("CodeView")).Document;
        Assert.Contains(status.ToString(),new TextRange(document.ContentStart,document.ContentEnd).Text);
        Assert.True(((TextBlock)viewer.FindName("TimeText")).Text.Length>0&&((TextBlock)viewer.FindName("SizeText")).Text.Length>0);
    });
    [Theory][InlineData("/malformed")][InlineData("/empty")]
    public Task Viewer_handles_malformed_and_empty_JSON(string path)=>WpfThread.Run(async()=>
    {
        await using var server=new QaServer();using var request=new HttpRequestMessage(HttpMethod.Get,server.Url+path);
        var result=await new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5));var viewer=new ResponseViewer();viewer.ShowResponse(result);
        var document=((RichTextBox)viewer.FindName("CodeView")).Document;var text=new TextRange(document.ContentStart,document.ContentEnd).Text;
        Assert.True(path=="/empty"?text.Contains("empty response body"):text.Contains("{not-json"));
    });
    [Theory][InlineData(true)][InlineData(false)]
    public Task UI_remains_responsive_during_cancellation_and_timeout(bool cancel)=>WpfThread.Run(async()=>
    {
        await using var server=new QaServer();var store=new WorkspaceStore(Path.Combine(SecurityStorageTests.NewRoot(),"workspace.json"));using var editor=new RequestEditor(store);
        var definition=HttpTests.Definition("GET",server.Url);definition.Url=server.Url+"/slow";definition.TimeoutSeconds=1;editor.Load(definition);
        var ticks=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};timer.Tick+=(_,_)=>ticks++;timer.Start();
        var send=(Button)editor.FindName("SendButton");send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(150);
        if(cancel)send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline=DateTime.UtcNow.AddSeconds(5);while(!Equals(send.Content,"Send")&&DateTime.UtcNow<deadline)await Task.Delay(20);timer.Stop();
        Assert.True(Equals(send.Content,"Send")&&ticks>=3,"Dispatcher did not remain responsive.");
        var viewer=(ResponseViewer)editor.FindName("ResponsePanel");var hint=((TextBlock)viewer.FindName("EmptyHint")).Text;
        Assert.True(cancel?hint.Contains("canceled"):hint.Contains("timed out"));
        Assert.True(store.Snapshot().History.Single().Error==hint);
    });
    [Fact]
    public Task Invalid_URL_shows_safe_UI_error()=>WpfThread.Run(()=>
    {
        var store=new WorkspaceStore(Path.Combine(SecurityStorageTests.NewRoot(),"workspace.json"));using var editor=new RequestEditor(store);
        editor.Load(new(){Url="not-a-valid-http-url"});((Button)editor.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var viewer=(ResponseViewer)editor.FindName("ResponsePanel");Assert.Contains("HTTP URL",((TextBlock)viewer.FindName("EmptyHint")).Text);return Task.CompletedTask;
    });
}
