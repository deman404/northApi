using System.Diagnostics;
using System.Windows.Automation;
using northApi.models;
using northApi.services;
using Xunit;

namespace ReleaseQA;
public class ProcessRestartTests
{
    [Fact]
    public async Task Application_saves_sends_closes_and_reopens_collections_history_environments()
    {
        var root=SecurityStorageTests.NewRoot();var path=Path.Combine(root,"workspace.json");var store=new WorkspaceStore(path);
        store.SaveEnvironment(new(){Name="QA restart environment",Variables=[new(){Key="baseUrl",Value="http://localhost"}]});
        await using var server=new QaServer();
        using(var first=Start(root))
        try
        {
            var window=await Window(first);
            Invoke(Find(window,"New HTTP Request"));
            var url=await Wait(()=>Find(window,"Request URL"));((ValuePattern)url.GetCurrentPattern(ValuePattern.Pattern)).SetValue(server.Url+"/echo");
            Invoke(Find(window,"Send"));
            await Until(()=>new WorkspaceStore(path).Snapshot().History.FirstOrDefault()?.StatusCode==200);
            Invoke(Find(window,"Save request"));
            var dialog=await Wait(()=>AutomationElement.RootElement.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,first.Id),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Window),new PropertyCondition(AutomationElement.NameProperty,"Save request"))));
            var inputs=dialog.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
            ((ValuePattern)inputs[0].GetCurrentPattern(ValuePattern.Pattern)).SetValue("Restart request");
            ((ValuePattern)inputs[1].GetCurrentPattern(ValuePattern.Pattern)).SetValue("Restart collection");
            Invoke(Find(dialog,"Save to collection"));
            await Until(()=>new WorkspaceStore(path).Snapshot().Collections.Count==1);
            first.CloseMainWindow();await Exit(first);
        }
        finally{if(!first.HasExited)first.Kill(entireProcessTree:true);}
        using(var second=Start(root))
        try
        {
            var window=await Window(second);Assert.NotNull(await Wait(()=>Find(window,"Restart collection")));
            Select(Find(window,"Environments"));Assert.NotNull(await Wait(()=>Find(window,"QA restart environment")));
            Select(Find(window,"History"));
            await Until(()=>window.FindAll(TreeScope.Descendants,Condition.TrueCondition).Cast<AutomationElement>().Any(element=>element.Current.Name.Contains("UTC")&&element.Current.Name.Contains("200")));
            var reloaded=new WorkspaceStore(path).Snapshot();Assert.Single(reloaded.Collections[0].Requests);Assert.Single(reloaded.History);Assert.Single(reloaded.Environments);
            second.CloseMainWindow();await Exit(second);
        }
        finally{if(!second.HasExited)second.Kill(entireProcessTree:true);}
    }
    private static Process Start(string dataDirectory)
    {
        var executable=Environment.GetEnvironmentVariable("NORTHAPI_QA_EXE");
        var info=new ProcessStartInfo{UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        if(executable is not null)
        {
            info.FileName=executable;
            var empty=Path.Combine(dataDirectory,"empty-dotnet-root");Directory.CreateDirectory(empty);
            info.Environment["DOTNET_ROOT"]=empty;info.Environment["DOTNET_ROOT_X64"]=empty;info.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";
        }
        else
        {
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"northApi.csproj")))root=root.Parent;
            Assert.NotNull(root);info.FileName="dotnet";info.ArgumentList.Add(Path.Combine(root!.FullName,"bin","Release","net10.0-windows","northApi.dll"));
        }
        info.Environment["NORTHAPI_DATA_DIRECTORY"]=dataDirectory;
        return Process.Start(info)!;
    }
    private static async Task<AutomationElement> Window(Process process)=>await Wait(()=>{process.Refresh();return process.MainWindowHandle!=IntPtr.Zero&&process.MainWindowTitle.Contains("Workspace")?AutomationElement.FromHandle(process.MainWindowHandle):null;});
    private static AutomationElement? Find(AutomationElement parent,string name)=>parent.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,name));
    private static void Invoke(AutomationElement? element){Assert.NotNull(element);((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();}
    private static void Select(AutomationElement? element){Assert.NotNull(element);((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();}
    private static async Task<T> Wait<T>(Func<T?> get)where T:class
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);while(DateTime.UtcNow<deadline){var result=get();if(result is not null)return result;await Task.Delay(100);}throw new TimeoutException("Application UI was not ready.");
    }
    private static async Task Until(Func<bool> complete){var deadline=DateTime.UtcNow.AddSeconds(15);while(DateTime.UtcNow<deadline){if(complete())return;await Task.Delay(100);}throw new TimeoutException("Application state did not complete.");}
    private static async Task Exit(Process process){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));await process.WaitForExitAsync(timeout.Token);Assert.Equal(0,process.ExitCode);}
}
