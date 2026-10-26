namespace BeastContest.Web.Models;

public sealed record LanguageProfile(string Name,string SourceName,string[]? Compile,int MinimumMemoryMb=16)
{
    public string[] Run(int memoryMb)=>Name switch{
        "C++" or "Go" or "Haskell"=>["/solution/main"],
        "Python"=>["python3","-I","-B","/solution/main.py"],
        "C#"=>["dotnet","exec","--runtimeconfig","/opt/judge/Solution.runtimeconfig.json","/solution/Solution.dll"],
        "Java"=>["java","-XX:+UseSerialGC","-XX:ActiveProcessorCount=1","-Xms16m","-Xmx"+Math.Max(16,memoryMb/2)+"m","-XX:ReservedCodeCacheSize=32m","-XX:MaxMetaspaceSize=64m","-Xss256k","-cp","/solution","Main"],
        "JavaScript"=>["node","--max-old-space-size="+Math.Max(16,memoryMb/2),"/solution/main.js"],
        _=>throw new InvalidOperationException("Unsupported language")
    };
}
public static class LanguageProfiles
{
    public static readonly LanguageProfile[] All=[
        new("C++","main.cpp",["g++","-std=c++20","-O2","-pipe","/solution/main.cpp","-o","/tmp/main"]),
        new("C#","Main.cs",["dotnet","/opt/judge/roslyn/csc.dll","-nologo","-noconfig","-nostdlib+","-target:exe","-optimize+","-out:/tmp/Solution.dll","@/opt/judge/references.rsp","/solution/Main.cs"],64),
        new("Java","Main.java",["javac","-J-Xmx512m","-J-XX:ActiveProcessorCount=2","-encoding","UTF-8","-d","/tmp","/solution/Main.java"],128),
        new("Python","main.py",null),
        new("Go","main.go",["go","build","-p","2","-o","/tmp/main","/solution/main.go"]),
        new("Haskell","Main.hs",["ghc","-O1","-outputdir","/tmp","-o","/tmp/main","/solution/Main.hs"]),
        new("JavaScript","main.js",null,64)
    ];
    public static LanguageProfile? Find(string name)=>All.FirstOrDefault(p=>p.Name==name);
}
