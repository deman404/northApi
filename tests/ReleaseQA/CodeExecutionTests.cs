using System.Diagnostics;
using System.Text;
using System.Text.Json;
using northApi.models;
using northApi.services;
using Xunit;

namespace ReleaseQA;

public class CodeExecutionTests
{
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public async Task Generated_CSharp_and_native_curl_execute_and_match_original_requests(int scenario)
    {
        await using var server=new QaServer();var root=SecurityStorageTests.NewRoot();var cases=new List<RequestDefinition>();
        cases.Add(HttpTests.Definition("GET",server.Url));
        var post=HttpTests.Definition("POST",server.Url);post.Body.Raw="{\"text\":\"apostrophe ' quote \\\" and text\"}";cases.Add(post);
        var basic=HttpTests.Definition("GET",server.Url);basic.Auth=new(){Type="basic",Username="qa",Password=Guid.NewGuid().ToString("N")};cases.Add(basic);
        var bearer=HttpTests.Definition("POST",server.Url);bearer.Auth=new(){Type="bearer",Token=Guid.NewGuid().ToString("N")};cases.Add(bearer);
        var headers=HttpTests.Definition("POST",server.Url);headers.Headers.Add(new(){Key="X-Quoted",Value="spaces ' quote \" dollar $ and `backtick`"});cases.Add(headers);
        var unicode=HttpTests.Definition("POST",server.Url);unicode.Body.Raw="{\"text\":\"café\"}";cases.Add(unicode);
        for(var index=scenario;index==scenario;index++)
        {
            var source=cases[index];using var original=RequestBuilder.Build(source);
            var originalResponse=await new HttpRequestEngine().SendAsync(original,TimeSpan.FromSeconds(5));var expected=JsonSerializer.Deserialize<WireRequest>(originalResponse.Body)!;
            var code=CodeGenerator.Generate(source);var folder=Path.Combine(root,"case"+index);Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder,"Snippet.csproj"),"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(folder,"Program.cs"),code.CSharp);
            var build=await Run("dotnet",["build",Path.Combine(folder,"Snippet.csproj"),"--configuration","Release","--nologo"]);
            Assert.True(build.Exit==0,"Generated C# did not compile; output withheld to avoid credential disclosure.");
            var csharp=await Run("dotnet",[Path.Combine(folder,"bin","Release","net10.0","Snippet.dll")]);
            Assert.True(csharp.Exit==0,"Generated C# execution failed; output withheld.");
            var actual=Parse(csharp.Output);Compare(expected,actual);
            // Execute the generated token stream verbatim with native curl. This is
            // POSIX quote decoding only, not a replacement implementation of curl.
            var tokens=PosixTokens(code.Curl);Assert.True(tokens[0]=="curl");
            var curl=await Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"curl.exe"),tokens.Skip(1).ToArray());
            Assert.True(curl.Exit==0,"Generated cURL execution failed; output withheld.");Compare(expected,Parse(curl.Output));
        }
    }
    [Theory]
    [InlineData("curl 'a b'", "a b")]
    [InlineData("curl 'a'\"'\"'b'", "a'b")]
    [InlineData("curl 'literal $x `y` \\ path'", "literal $x `y` \\ path")]
    public void POSIX_literal_quote_decoding_matches_generator(string command,string expected)=>Assert.True(PosixTokens(command)[1]==expected);
    private static WireRequest Parse(string output)
    {
        var start=output.IndexOf('{');Assert.True(start>=0,"Echo response missing; output withheld.");
        return JsonSerializer.Deserialize<WireRequest>(output[start..])!;
    }
    private static void Compare(WireRequest expected,WireRequest actual)
    {
        Assert.True(expected.Method==actual.Method&&expected.Target==actual.Target,"Generated method or URL differs.");
        Assert.True(expected.Body==actual.Body,"Generated body differs; values withheld.");
        foreach(var header in expected.Headers.Where(header=>header.Key.StartsWith("X-",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Authorization",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Content-Type",StringComparison.OrdinalIgnoreCase)))
            Assert.True(actual.Headers.TryGetValue(header.Key,out var value)&&value==header.Value,"Generated application header differs; values withheld.");
    }
    internal static List<string> PosixTokens(string input)
    {
        var result=new List<string>();var token=new StringBuilder();var mode='\0';var started=false;
        for(var i=0;i<input.Length;i++)
        {
            var ch=input[i];
            if(mode=='\''){if(ch=='\'')mode='\0';else token.Append(ch);continue;}
            if(mode=='"'){if(ch=='"')mode='\0';else if(ch=='\\'&&i+1<input.Length&&"$`\"\\\n".Contains(input[i+1]))token.Append(input[++i]);else token.Append(ch);continue;}
            if(ch=='\''||ch=='"'){mode=ch;started=true;continue;}
            if(char.IsWhiteSpace(ch)){if(started){result.Add(token.ToString());token.Clear();started=false;}continue;}
            if(ch=='\\'){token.Append(input[++i]);started=true;continue;}
            if(";&|<>$()`".Contains(ch))throw new InvalidOperationException("Unsupported unquoted shell syntax.");
            token.Append(ch);started=true;
        }
        if(mode!='\0')throw new InvalidOperationException("Unterminated quote.");if(started)result.Add(token.ToString());return result;
    }
    private static async Task<(int Exit,string Output)> Run(string exe,string[] args)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(entireProcessTree:true);throw new TimeoutException("Generated example timed out.");}
        await stderr;return(process.ExitCode,await stdout);
    }
}
