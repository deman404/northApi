using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using northApi.components;
using northApi.models;
using northApi.pages;
using northApi.services;

internal static class Program
{
    [DllImport("user32.dll")]private static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr hwnd,out RectNative rect);
    [StructLayout(LayoutKind.Sequential)]private struct RectNative{public int Left,Top,Right,Bottom;}
    [STAThread]private static void Main(string[] args)
    {
        var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        var isolated=Path.Combine(Path.GetTempPath(),"NorthApiReadme",Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NORTHAPI_DATA_DIRECTORY",isolated);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(_,_)=>
        {
            try{await Capture(app,output);app.Shutdown(0);}
            catch(Exception exception){Console.Error.WriteLine("Screenshot capture failed: "+exception.GetType().Name);app.Shutdown(1);}
        };
        app.Run();
    }
    private static async Task Capture(Application app,string output)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var baseUrl=$"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var server=Serve(listener);
        var environment=new EnvironmentProfile{Name="Local demo",Variables=[new(){Key="baseUrl",Value=baseUrl}]};
        var get=new RequestDefinition{Name="Get workspace",Method="GET",Url="{{baseUrl}}/workspace",EnvironmentId=environment.Id,Body=new(){Mode="none"},Headers=[new(){Key="Accept",Value="application/json"}]};
        var post=new RequestDefinition{Name="Create a request",Method="POST",Url="{{baseUrl}}/requests",EnvironmentId=environment.Id,Body=new(){Mode="raw",ContentType="application/json",Raw="{\n  \"name\": \"My first API request\",\n  \"method\": \"GET\",\n  \"url\": \"{{baseUrl}}/workspace\",\n  \"tags\": [\"demo\", \"local-first\"]\n}"}};
        WorkspaceStore.Shared.SaveEnvironment(environment);WorkspaceStore.Shared.SaveRequest(get,"Workspace API");WorkspaceStore.Shared.SaveRequest(post,"Workspace API");
        var window=new WorkspaceWindow{Width=1440,Height=900,WindowStartupLocation=WindowStartupLocation.CenterScreen};app.MainWindow=window;window.Show();
        await Task.Delay(250);
        var sidebar=(Sidebar)window.FindName("NavigationSidebar");var list=(StackPanel)sidebar.FindName("CollectionList");foreach(var expander in list.Children.OfType<Expander>())expander.IsExpanded=true;
        var editor=Open(window,get);await Send(editor);await Task.Delay(150);SaveWindow(window,Path.Combine(output,"workspace.png"));
        var body=Open(window,post);((TabControl)body.FindName("RequestOptions")).SelectedIndex=3;await Send(body);await Task.Delay(150);SaveWindow(window,Path.Combine(output,"request-body.png"));
        ((AuthorizationEditor)body.FindName("Authorization")).Load(new(){Type="basic",Username="demo-user",Password="demo-only-password"});((TabControl)body.FindName("RequestOptions")).SelectedIndex=1;
        await Task.Delay(150);SaveWindow(window,Path.Combine(output,"authorization.png"));
        DrawBanner(output);window.Close();listener.Stop();
        Console.WriteLine("Captured workspace.png, request-body.png, authorization.png, and social-preview.png using isolated local demo data.");
    }
    private static RequestEditor Open(WorkspaceWindow window,RequestDefinition request)
    {
        typeof(WorkspaceWindow).GetMethod("OpenRequest",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[request]);
        return (RequestEditor)((TabItem)((TabControl)window.FindName("RequestTabs")).SelectedItem).Content;
    }
    private static async Task Send(RequestEditor editor)
    {
        var button=(Button)editor.FindName("SendButton");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var timeout=DateTime.UtcNow.AddSeconds(8);while(!Equals(button.Content,"Send")&&DateTime.UtcNow<timeout)await Task.Delay(20);
        var panel=(ResponseViewer)editor.FindName("ResponsePanel");if(!((TextBlock)panel.FindName("StatusText")).Text.StartsWith("200"))throw new InvalidOperationException("Demo HTTP request did not complete.");
    }
    private static async Task Serve(TcpListener listener)
    {
        for(var i=0;i<2;i++)
        {
            using var client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.UTF8,leaveOpen:true);
            var first=await reader.ReadLineAsync().ConfigureAwait(false);var length=0;while(true){var line=await reader.ReadLineAsync().ConfigureAwait(false);if(string.IsNullOrEmpty(line))break;if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(line.Split(':')[1]);}
            if(length>0){var chars=new char[length];await reader.ReadBlockAsync(chars,0,length).ConfigureAwait(false);}
            var json=first!.StartsWith("GET")?"{\"workspace\":{\"name\":\"NorthAPI\",\"mode\":\"local-first\",\"status\":\"connected\"},\"features\":[\"requests\",\"collections\",\"history\",\"environments\"],\"storage\":{\"provider\":\"Windows DPAPI\",\"scope\":\"CurrentUser\"},\"message\":\"Your next request starts here.\"}":"{\"id\":\"req_demo_001\",\"name\":\"My first API request\",\"method\":\"GET\",\"saved\":true,\"tags\":[\"demo\",\"local-first\"]}";
            var bytes=Encoding.UTF8.GetBytes(json);var headers=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nX-Demo: NorthAPI\r\nConnection: close\r\n\r\n");await stream.WriteAsync(headers).ConfigureAwait(false);await stream.WriteAsync(bytes).ConfigureAwait(false);
        }
    }
    private static void SaveWindow(Window window,string path)
    {
        window.UpdateLayout();var handle=new WindowInteropHelper(window).Handle;GetWindowRect(handle,out var rect);
        using var bitmap=new System.Drawing.Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top);using var graphics=System.Drawing.Graphics.FromImage(bitmap);var dc=graphics.GetHdc();
        try{if(!PrintWindow(handle,dc,2))throw new InvalidOperationException("Window capture unavailable.");}finally{graphics.ReleaseHdc(dc);}
        bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
    }
    private static Brush Brush(string value)=>(Brush)new BrushConverter().ConvertFromString(value)!;
    private static void Text(DrawingContext dc,string value,double x,double y,double size,string color,FontWeight? weight=null)
    {
        var formatted=new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,weight??FontWeights.Normal,FontStretches.Normal),size,Brush(color),1);dc.DrawText(formatted,new Point(x,y));
    }
    private static void DrawBanner(string output)
    {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(13,19,25),Color.FromRgb(25,35,42),20),null,new Rect(0,0,1280,640));
            for(var x=40;x<1280;x+=40)for(var y=40;y<640;y+=40)dc.DrawEllipse(Brush("#28343D"),null,new Point(x,y),1,1);
            dc.DrawRoundedRectangle(Brush("#182A30"),new Pen(Brush("#2B444C"),1),new Rect(64,60,182,32),16,16);Text(dc,"LOCAL-FIRST API CLIENT",80,67,11,"#A1D8DD",FontWeights.SemiBold);
            dc.DrawEllipse(Brush("#FF6C37"),null,new Point(89,159),25,25);
            var icon=new System.Xml.XmlDocument();icon.Load(Path.GetFullPath("assets/compass.svg"));var data=icon.GetElementsByTagName("path")[0]!.Attributes!["d"]!.Value;
            dc.PushTransform(new TranslateTransform(74,144));dc.PushTransform(new ScaleTransform(1.25,1.25));dc.DrawGeometry(Brush("#FFFFFF"),null,Geometry.Parse(data));dc.Pop();dc.Pop();
            Text(dc,"NorthAPI",130,119,53,"#F2F5F5",FontWeights.SemiBold);
            Text(dc,"Your APIs.",64,222,51,"#F2F5F5",FontWeights.SemiBold);Text(dc,"Your workspace.",64,284,51,"#F2F5F5",FontWeights.SemiBold);
            Text(dc,"Build requests. Read responses.\nKeep your work local.",67,369,22,"#A4B5BD");
            Text(dc,"WINDOWS  /  .NET 10  /  WPF",67,502,13,"#8CA0AC",FontWeights.SemiBold);
            Text(dc,"github.com/deman404/NorthAPI",67,558,14,"#718C9B");
            var frame=new Rect(570,90,650,430);dc.DrawRoundedRectangle(Brush("#0B1014"),new Pen(Brush("#3F505A"),1),new Rect(564,84,662,442),10,10);
            var screenshot=new BitmapImage(new Uri(Path.Combine(output,"workspace.png")));dc.PushClip(new RectangleGeometry(frame,7,7));dc.DrawImage(screenshot,frame);dc.Pop();
            dc.DrawRoundedRectangle(Brush("#16352D"),new Pen(Brush("#325747"),1),new Rect(1004,505,174,44),8,8);Text(dc,"200 OK  ·  YOUR NEXT API",1018,520,10,"#8CE6B7",FontWeights.SemiBold);
        }
        var render=new RenderTargetBitmap(1280,640,96,96,PixelFormats.Pbgra32);render.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(render));using var file=File.Create(Path.Combine(output,"social-preview.png"));encoder.Save(file);
    }
}
