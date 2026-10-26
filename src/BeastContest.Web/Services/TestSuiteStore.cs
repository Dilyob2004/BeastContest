using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BeastContest.Web.Data;
using BeastContest.Web.Models;
using Microsoft.EntityFrameworkCore;
namespace BeastContest.Web.Services;

public sealed class TestCaseDraft
{
    [JsonRequired] public string Input {get;set;}="";
    [JsonRequired] public string Output {get;set;}="";
    /// <summary>Weight in points-mode contests (0–1000). Zero suits sample tests.</summary>
    public int Points {get;set;}=1;
    /// <summary>Optional group name; a group scores only when all of its tests pass.</summary>
    public string Group {get;set;}="";
}
public sealed record TestSuite(int Revision,TestCaseDraft[] Tests);
public sealed class TestSuiteStore(IDbContextFactory<AppDbContext> factory,ICurrentActor actor)
{
    public const int MaxFileBytes=4*1024*1024;
    public const int MaxTextBytes=262144;
    public static string ParseText(string filename,byte[] bytes){
        if(!filename.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)||bytes.Length>MaxTextBytes)throw new DomainException("tests.txtInvalid");
        try{
            var offset=bytes.AsSpan().StartsWith(new byte[]{239,187,191})?3:0;
            var text=new UTF8Encoding(false,true).GetString(bytes,offset,bytes.Length-offset);
            if(text.Contains('\0'))throw new DomainException("tests.txtInvalid");
            return text;
        }catch(DecoderFallbackException){throw new DomainException("tests.txtInvalid");}
    }
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static TestCaseDraft[] Parse(string json){
        if(Encoding.UTF8.GetByteCount(json)>MaxFileBytes)throw new DomainException("tests.invalid");
        try{var tests=JsonSerializer.Deserialize<TestCaseDraft[]>(json,JsonOptions);Validate(tests);return tests!;}
        catch(JsonException){throw new DomainException("tests.invalid");}
    }
    public static void Validate(TestCaseDraft[]? tests){
        if(tests is null || tests.Length>100 || tests.Any(t=>t is null || t.Input is null || t.Output is null || t.Input.Contains('\0') || t.Output.Contains('\0') || Encoding.UTF8.GetByteCount(t.Input)>262144 || Encoding.UTF8.GetByteCount(t.Output)>262144) || tests.Sum(t=>(long)Encoding.UTF8.GetByteCount(t.Input)+Encoding.UTF8.GetByteCount(t.Output))>MaxFileBytes)
            throw new DomainException("tests.invalid");
        if(tests.Any(t=>t.Points is <0 or >TestScoring.MaxPoints || !TestScoring.ValidGroup(t.Group)))throw new DomainException("tests.pointsInvalid");
        if(tests.Length>0 && tests.Sum(t=>(long)t.Points)==0)throw new DomainException("tests.pointsTotal");
    }
    public const int MaxZipBytes=16*1024*1024;
    private const int MaxZipEntries=1000;
    /// <summary>
    /// Reads input/answer pairs from a ZIP archive without writing anything to disk.
    /// Supported names: NAME.in + NAME.out|NAME.ans, NAME + NAME.a, inputNAME.txt + outputNAME.txt,
    /// and input/NAME + output/NAME folders. Tests are ordered naturally by NAME.
    /// </summary>
    public static TestCaseDraft[] ParseZip(byte[] bytes){
        if(bytes.Length>MaxZipBytes)throw new DomainException("tests.zipInvalid");
        var inputs=new Dictionary<string,string>(StringComparer.Ordinal);var outputs=new Dictionary<string,string>(StringComparer.Ordinal);
        long total=0;
        try{
            using var archive=new System.IO.Compression.ZipArchive(new MemoryStream(bytes,false),System.IO.Compression.ZipArchiveMode.Read);
            if(archive.Entries.Count>MaxZipEntries)throw new DomainException("tests.zipInvalid");
            foreach(var entry in archive.Entries){
                var path=entry.FullName.Replace('\\','/');
                if(entry.Name.Length==0 || path.StartsWith("__MACOSX/",StringComparison.Ordinal) || entry.Name.StartsWith('.'))continue;
                var role=Classify(path,out var key);
                if(role==0)continue;
                if(entry.Length>MaxTextBytes)throw new DomainException("tests.zipInvalid");
                total+=entry.Length;if(total>MaxFileBytes)throw new DomainException("tests.zipInvalid");
                using var stream=entry.Open();using var memory=new MemoryStream();
                var buffer=new byte[81920];int read;
                // The declared size can lie; never read more than the per-file limit.
                while((read=stream.Read(buffer,0,buffer.Length))>0){if(memory.Length+read>MaxTextBytes)throw new DomainException("tests.zipInvalid");memory.Write(buffer,0,read);}
                var text=ParseText(entry.Name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)?entry.Name:entry.Name+".txt",memory.ToArray());
                var target=role==1?inputs:outputs;
                if(!target.TryAdd(key,text))throw new DomainException("tests.zipDuplicate");
            }
        }catch(InvalidDataException){throw new DomainException("tests.zipInvalid");}
        catch(DomainException ex) when(ex.Keys.Contains("tests.txtInvalid")){throw new DomainException("tests.zipInvalid");}
        if(inputs.Count==0 || !inputs.Keys.Order().SequenceEqual(outputs.Keys.Order()))throw new DomainException("tests.zipUnpaired");
        var tests=inputs.Keys.Order(NaturalComparer.Instance).Select(k=>new TestCaseDraft{Input=inputs[k],Output=outputs[k]}).ToArray();
        Validate(tests);return tests;
    }
    /// <returns>1 for input, 2 for answer, 0 for unrelated files.</returns>
    private static int Classify(string path,out string key){
        var slash=path.LastIndexOf('/');var dir=slash<0?"":path[..(slash+1)];var name=path[(slash+1)..];
        var lower=name.ToLowerInvariant();
        string Stem(int cut)=>dir+name[..^cut];
        var parent=dir.TrimEnd('/');var parentSlash=parent.LastIndexOf('/');var folder=(parentSlash<0?parent:parent[(parentSlash+1)..]).ToLowerInvariant();
        var root=parentSlash<0?"":parent[..(parentSlash+1)];
        if(folder is "input" or "inputs" or "in"){key=root+"|"+StripTxt(name);return 1;}
        if(folder is "output" or "outputs" or "out" or "answer" or "answers"){key=root+"|"+StripTxt(name);return 2;}
        if(lower.EndsWith(".in")){key=Stem(3);return 1;}
        if(lower.EndsWith(".out")){key=Stem(4);return 2;}
        if(lower.EndsWith(".ans")){key=Stem(4);return 2;}
        if(lower.EndsWith(".a")){key=Stem(2);return 2;}
        var m=Regex.Match(name,"^(input|output)(.*)\\.txt$",RegexOptions.IgnoreCase);
        if(m.Success){key=dir+"|io|"+m.Groups[2].Value;return m.Groups[1].Value.Equals("input",StringComparison.OrdinalIgnoreCase)?1:2;}
        if(!name.Contains('.')){key=dir+name;return 1;}
        key="";return 0;
    }
    private static string StripTxt(string name)=>name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)?name[..^4]:name;
    private sealed class NaturalComparer:IComparer<string>{
        public static readonly NaturalComparer Instance=new();
        public int Compare(string? x,string? y){
            var a=Regex.Split(x??"","(\\d+)");var b=Regex.Split(y??"","(\\d+)");
            for(var i=0;i<Math.Min(a.Length,b.Length);i++){
                int c;
                if(a[i].Length>0&&b[i].Length>0&&char.IsDigit(a[i][0])&&char.IsDigit(b[i][0])){
                    var ta=a[i].TrimStart('0');var tb=b[i].TrimStart('0');
                    c=ta.Length!=tb.Length?ta.Length.CompareTo(tb.Length):string.CompareOrdinal(ta,tb);
                    if(c==0)c=a[i].Length.CompareTo(b[i].Length);
                }else c=string.CompareOrdinal(a[i],b[i]);
                if(c!=0)return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
    public async Task<TestSuite> GetAsync(string id){
        await actor.RequireAdminAsync();await using var db=await factory.CreateDbContextAsync();
        if(!await db.Problems.AnyAsync(p=>p.Id==id))throw new DomainException("common.notfound");
        var suite=await db.ProblemTests.AsNoTracking().SingleOrDefaultAsync(t=>t.ProblemId==id);
        return suite is null?new(0,[]):new(suite.Revision,JsonSerializer.Deserialize<TestCaseDraft[]>(suite.TestsJson)!);
    }
    public async Task<int> SaveAsync(string id,int revision,TestCaseDraft[] tests){
        await actor.RequireAdminAsync();Validate(tests);await using var db=await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        // Contest rows first, then the problem: the same order as rejudge and submission.
        var pending=await ContestStore.LockPendingContestsAsync(db,id);
        if(await db.Problems.FromSqlInterpolated($"SELECT * FROM \"Problems\" WHERE \"Id\"={id} FOR SHARE").SingleOrDefaultAsync() is null)throw new DomainException("common.notfound");
        var suite=await db.ProblemTests.SingleOrDefaultAsync(t=>t.ProblemId==id);
        if((suite?.Revision ?? 0)!=revision)throw new DomainException("tests.conflict");
        if(suite is null){suite=new ProblemTestEntity{ProblemId=id};db.ProblemTests.Add(suite);}
        suite.TestsJson=JsonSerializer.Serialize(tests);suite.Revision++;
        try{await db.SaveChangesAsync();}catch(DbUpdateConcurrencyException){throw new DomainException("tests.conflict");}catch(DbUpdateException ex) when(ex.InnerException is Npgsql.PostgresException {SqlState:"23505"}){throw new DomainException("tests.conflict");}
        // Scheduled contests that have not started yet judge with the tests saved last.
        await ContestStore.RefreshSnapshotsAsync(db,pending);
        await tx.CommitAsync();return suite.Revision;
    }
}


