using BeastContest.Web.Services;
namespace BeastContest.Judge;
internal static class RunnerChecks
{
    public static async Task RunAsync(string? language=null){
        var runner=new DockerRunner("beastcontest-sandbox:local");
        var problem=new JudgeProblem("check",1,2000,256,[new(){Input="2 3\n",Output="5\n"}]);
        (string Language,string Source,string Expected)[] cases=[
            ("Python","a,b=map(int,input().split());print(a+b)","Accepted"),
            ("Python","print(0)","WrongAnswer"),
            ("Python","raise Exception('test')","RuntimeError"),
            ("Python","while True: pass","TimeLimitExceeded"),
            ("Python","print('x'*2000000)","OutputLimitExceeded"),
            ("Python","a=bytearray(600*1024*1024)","MemoryLimitExceeded"),
            ("C++","#include <iostream>\nint main(){long long a,b;std::cin>>a>>b;std::cout<<a+b;}","Accepted"),
            ("C++","this is not C++","CompilationError"),
            ("Python","import os,socket\nassert os.getuid()!=0\nassert not os.path.exists('/var/run/docker.sock')\nassert not any('Database' in k for k in os.environ)\ntry:\n open('/solution/leak','w');raise AssertionError('writable')\nexcept OSError: pass\ns=socket.socket();s.settimeout(.2)\nassert s.connect_ex(('1.1.1.1',80))!=0\nprint(5)","Accepted")
        ];
        (string Language,string Source,string Expected)[] additional=[
            ("C#","using System;var a=Array.ConvertAll(Console.ReadLine()!.Split(),long.Parse);Console.WriteLine(a[0]+a[1]);","Accepted"),
            ("C#","System.Console.WriteLine(0);","WrongAnswer"),
            ("C#","throw new System.Exception();","RuntimeError"),
            ("C#","while(true){}","TimeLimitExceeded"),
            ("C#","not valid C#","CompilationError"),
            ("Java","import java.util.*; public class Main {static class Sum {long add(long a,long b){return a+b;}} public static void main(String[] args){Scanner s=new Scanner(System.in);System.out.println(new Sum().add(s.nextLong(),s.nextLong()));}}","Accepted"),
            ("Java","public class Main {public static void main(String[] a){System.out.println(0);}}","WrongAnswer"),
            ("Java","public class Main {public static void main(String[] a){throw new RuntimeException();}}","RuntimeError"),
            ("Java","public class Main {public static void main(String[] a){while(true){}}}","TimeLimitExceeded"),
            ("Java","this is not java","CompilationError"),
            ("Go","package main\nimport \"fmt\"\nfunc main(){var a,b int64;fmt.Scan(&a,&b);fmt.Println(a+b)}","Accepted"),
            ("Go","package main\nimport \"fmt\"\nfunc main(){fmt.Println(0)}","WrongAnswer"),
            ("Go","package main\nfunc main(){panic(\"test\")}","RuntimeError"),
            ("Go","package main\nfunc main(){for {}}","TimeLimitExceeded"),
            ("Go","this is not Go","CompilationError"),
            ("Haskell","main = interact (show . sum . map (read :: String -> Integer) . words)","Accepted"),
            ("Haskell","main = print (0 :: Int)","WrongAnswer"),
            ("Haskell","main = error \"test\"","RuntimeError"),
            ("Haskell","import Control.Monad\nmain = forever (return ())","TimeLimitExceeded"),
            ("Haskell","this is not Haskell","CompilationError"),
            ("JavaScript","const a=require('fs').readFileSync(0,'utf8').trim().split(/\\s+/).map(Number);console.log(a[0]+a[1]);","Accepted"),
            ("JavaScript","console.log(0)","WrongAnswer"),
            ("JavaScript","throw new Error('test')","RuntimeError"),
            ("JavaScript","while(true){}","TimeLimitExceeded"),
            ("JavaScript","const = ;","RuntimeError"),
            // Runtime heap limits are reported as MLE in every language, not as RE.
            ("Java","public class Main {public static void main(String[] a){long[][] keep=new long[1000][];for(int i=0;i<1000;i++)keep[i]=new long[1<<20];System.out.println(keep.length);}}","MemoryLimitExceeded"),
            ("C#","var keep=new System.Collections.Generic.List<long[]>();while(true)keep.Add(new long[1<<20]);","MemoryLimitExceeded"),
            ("JavaScript","const keep=[];while(true)keep.push(new Array(1<<20).fill(1.5));","MemoryLimitExceeded"),
            ("Python","bytearray(1<<40)","MemoryLimitExceeded"),
            ("C++","#include <vector>\nint main(){std::vector<char> v(1LL<<40);return v[0];}","MemoryLimitExceeded")
        ];
        foreach(var c in cases.Concat(additional).Where(c=>language is null||c.Language==language)){var result=await runner.JudgeAsync(c.Language,c.Source,problem,CancellationToken.None);if(result.Status!=c.Expected)throw new Exception($"{c.Language}: expected {c.Expected}, got {result.Status}: {result.Diagnostic}");Console.WriteLine($"PASS {c.Language}: {c.Expected}");}
        if(language is null||language=="Python")await CheckersAsync(runner);
    }
    private static async Task CheckersAsync(DockerRunner runner){
        async Task Expect(string name,JudgeProblem problem,string source,string status,int? score=null){
            var result=await runner.JudgeAsync("Python",source,problem,CancellationToken.None,revealCheckerErrors:true);
            if(result.Status!=status||(score is {} s&&result.Score!=s))throw new Exception($"{name}: expected {status}/{score}, got {result.Status}/{result.Score}: {result.Diagnostic}");
            Console.WriteLine($"PASS checker {name}: {status}"+(score is null?"":$" score {score}"));
        }
        TestCaseDraft[] third=[new(){Input="1 3\n",Output="0.333333333\n"}];
        var precise=new JudgeProblem("float",1,2000,256,third,new(CheckerModes.Float,1e-6));
        await Expect("float within epsilon",precise,"a,b=map(int,input().split());print(a/b)","Accepted");
        await Expect("float outside epsilon",precise,"print(0.3334)","WrongAnswer");
        var lines=new JudgeProblem("lines",1,2000,256,[new(){Input="",Output="a b\nc\n"}],new(CheckerModes.Lines));
        await Expect("lines ignore trailing spaces",lines,"print('a b  ');print('c');print()","Accepted");
        await Expect("lines keep line breaks",lines,"print('a');print('b c')","WrongAnswer");
        const string sumChecker="#include \"testlib.h\"\nint main(int argc,char*argv[]){registerTestlibCmd(argc,argv);long long a=inf.readLong(),b=inf.readLong();long long got=ouf.readLong();if(got!=a+b)quitf(_wa,\"expected %lld\",a+b);quitf(_ok,\"sum\");}";
        TestCaseDraft[] sums=[new(){Input="2 3\n",Output="ignored\n"}];
        await Expect("testlib accepted",new("testlib",1,2000,256,sums,new(CheckerModes.Testlib,Source:sumChecker)),"a,b=map(int,input().split());print(a+b)","Accepted");
        await Expect("testlib wrong answer",new("testlib",1,2000,256,sums,new(CheckerModes.Testlib,Source:sumChecker)),"print(4)","WrongAnswer");
        await Expect("testlib compile error",new("testlib",1,2000,256,sums,new(CheckerModes.Testlib,Source:"not c++")),"print(5)","SystemError");
        const string failing="#include \"testlib.h\"\nint main(int argc,char*argv[]){registerTestlibCmd(argc,argv);quitf(_fail,\"broken\");}";
        await Expect("testlib failure",new("testlib",1,2000,256,sums,new(CheckerModes.Testlib,Source:failing)),"print(5)","SystemError");
        TestCaseDraft[] weighted=[
            new(){Input="2 3\n",Output="5\n",Points=0},
            new(){Input="1 1\n",Output="2\n",Points=30,Group="g"},
            new(){Input="0 0\n",Output="1\n",Points=30,Group="g"},
            new(){Input="4 4\n",Output="8\n",Points=40}];
        await Expect("weights and groups",new("weights",1,2000,256,weighted),"a,b=map(int,input().split());print(a+b)","WrongAnswer",40);
    }
}

