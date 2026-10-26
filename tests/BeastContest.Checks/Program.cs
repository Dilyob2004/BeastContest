using Microsoft.AspNetCore.DataProtection;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeastContest.Web.Data;
using BeastContest.Web.Models;
using BeastContest.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

var root=Directory.GetCurrentDirectory();
var secretLine=File.ReadAllLines(Path.Combine(root,".env")).Single(l=>l.StartsWith("BEAST_DB_PASSWORD="));
var schema="checks_"+Guid.NewGuid().ToString("N");
var connection=new NpgsqlConnectionStringBuilder {Host="localhost",Port=54329,Database="beastcontest",Username="beastcontest",Password=secretLine["BEAST_DB_PASSWORD=".Length..],SearchPath=schema}.ConnectionString;
var folder=Path.Combine(root,".local","checks",schema);
Directory.CreateDirectory(folder);
var count=0;
void Check(bool pass,string message){if(!pass)throw new InvalidOperationException(message);Console.WriteLine("PASS "+message);count++;}
await using(var sql=new NpgsqlConnection(connection)){await sql.OpenAsync();await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"",sql).ExecuteNonQueryAsync();}
var legacy=new Contest(Guid.NewGuid(),new("Старый черновик","Legacy draft"),new("",""),ScoringMode.Icpc,120,100,["Python"],["sum"],false,DateTimeOffset.UtcNow);
await File.WriteAllTextAsync(Path.Combine(folder,"contests.json"),JsonSerializer.Serialize(new[]{legacy}));
var project=Path.Combine(root,"src","BeastContest.Web");
var start=new ProcessStartInfo("dotnet") {WorkingDirectory=project,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
start.ArgumentList.Add(Path.Combine(project,"bin","Debug","net10.0","BeastContest.Web.dll"));start.ArgumentList.Add("--urls");start.ArgumentList.Add("http://127.0.0.1:5081");
start.Environment["ConnectionStrings__Database"]=connection;
start.Environment["Storage__Path"]=folder;
start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";
start.Environment["Logging__LogLevel__Default"]="Warning";
var server=Process.Start(start)!;
var stdout=server.StandardOutput.ReadToEndAsync();var stderr=server.StandardError.ReadToEndAsync();
var keep=args.Contains("--serve");
using var admin=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}) {BaseAddress=new Uri("http://127.0.0.1:5081")};
using var guest=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}) {BaseAddress=admin.BaseAddress};
using var participant=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}) {BaseAddress=admin.BaseAddress};
var adminPassword="Checks-"+Guid.NewGuid().ToString("N");
var participantPassword=AccountService.GenerateParticipantPassword();
try{
    var ready=false;
    for(var i=0;i<60;i++){try{if((await guest.GetAsync("/health")).IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}await Task.Delay(300);}
    if(!ready)throw new Exception("Test server failed to start.");
    string Field(string html,string name){
        var tag=Regex.Matches(html,"<input[^>]*>").Select(m=>m.Value).Single(t=>Regex.IsMatch(t,$"name=\"{Regex.Escape(name)}\""));
        return WebUtility.HtmlDecode(Regex.Match(tag,"value=\"([^\"]*)\"").Groups[1].Value);
    }
    async Task<HttpResponseMessage> PostForm(HttpClient client,string get,string post,Dictionary<string,string> data){
        var html=await client.GetStringAsync(get);data["__RequestVerificationToken"]=Field(html,"__RequestVerificationToken");
        return await client.PostAsync(post,new FormUrlEncodedContent(data));
    }
    var provision=new ProcessStartInfo("dotnet") {WorkingDirectory=project,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
    provision.ArgumentList.Add(Path.Combine(project,"bin","Debug","net10.0","BeastContest.Web.dll"));
    foreach(var arg in new[]{"--create-user","admin_checks","--role","Admin","--name","Test organizer"}) provision.ArgumentList.Add(arg);
    provision.Environment["ConnectionStrings__Database"]=connection; provision.Environment["Storage__Path"]=folder;
    using(var command=Process.Start(provision)!) {
        var output=command.StandardOutput.ReadToEndAsync(); var error=command.StandardError.ReadToEndAsync();
        await command.StandardInput.WriteLineAsync(adminPassword); command.StandardInput.Close(); await command.WaitForExitAsync();
        Check(command.ExitCode==0,"Administrator is created by the server command."); await output; await error;
    }
    provision.ArgumentList[1]="--reset-admin";
    var replacementPassword="ResetAdmin-"+Guid.NewGuid().ToString("N");
    using(var command=Process.Start(provision)!) {
        var output=command.StandardOutput.ReadToEndAsync(); var error=command.StandardError.ReadToEndAsync();
        await command.StandardInput.WriteLineAsync(replacementPassword); command.StandardInput.Close(); await command.WaitForExitAsync();
        Check(command.ExitCode==0,"Server command resets an existing administrator password."); await output; await error;
    }
    var previousPasswordLogin=await PostForm(guest,"/account/login","/account/login",new(){{"Login","admin_checks"},{"Password",adminPassword}});
    Check(previousPasswordLogin.StatusCode==HttpStatusCode.OK && !previousPasswordLogin.Headers.Contains("Set-Cookie"),"Previous administrator password is rejected after CLI reset.");
    adminPassword=replacementPassword;
    foreach(var path in new[]{"/","/problems","/standings","/account/setup","/account/register"}) {
        var response=await guest.GetAsync(path);
        Check(response.StatusCode==HttpStatusCode.Redirect && response.Headers.Location!.ToString().Contains("/account/login"),"Guest is sent to login: "+path);
    }
    Check((await guest.GetAsync("/account/login")).IsSuccessStatusCode,"Login remains available without registration or setup.");
    var withoutCsrf=await guest.PostAsync("/account/login",new FormUrlEncodedContent(new Dictionary<string,string>{{"Login","admin_checks"},{"Password",adminPassword}}));
    Check(withoutCsrf.StatusCode==HttpStatusCode.BadRequest,"Missing antiforgery token is rejected.");
    var login=await PostForm(admin,"/account/login","/account/login",new(){{"Login","admin_checks"},{"Password",adminPassword}});
    Check(login.StatusCode==HttpStatusCode.Redirect && login.Headers.Contains("Set-Cookie"),"Administrator receives an authentication cookie.");
    var unauthorized=await guest.GetAsync("/admin/users");
    Check(unauthorized.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,"Anonymous requests cannot access administration.");

    var services=new ServiceCollection();
    services.AddLogging(b=>b.SetMinimumLevel(LogLevel.Warning));
    services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(folder,"test-keys")));
    services.AddDbContextFactory<AppDbContext>(o=>o.UseNpgsql(connection));
    services.AddIdentityCore<ApplicationUser>(o=>{o.Password.RequiredLength=6;o.Password.RequireDigit=false;o.Password.RequireLowercase=false;o.Password.RequireUppercase=false;o.Password.RequireNonAlphanumeric=false;})
        .AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
    using var provider=services.BuildServiceProvider();
    var factory=provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var identityScope=provider.CreateScope();
    var manager=identityScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var adminUser=(await manager.FindByNameAsync("admin_checks"))!;
    var adminActor=new FixedActor(new(adminUser.Id,adminUser.DisplayName,true));
    var accounts=new AccountService(adminActor,provider.GetRequiredService<IServiceScopeFactory>());
    await accounts.CreateAsync("player_checks","Test participant",participantPassword,"Participant");
    var player=(await manager.FindByNameAsync("player_checks"))!;
    var memberActor=new FixedActor(new(player.Id,player.DisplayName,false));
    var guestActor=new FixedActor(Actor.Guest);
    Check(LanguageProfiles.All.Select(p=>p.Name).Order().SequenceEqual(DemoCatalog.Languages.Order()),"Every offered language has a judge profile.");
    try{await new JudgeMonitorStore(factory,memberActor).GetAsync();throw new Exception("Participant accessed judge monitor.");}catch(UnauthorizedAccessException){Check(true,"Participant cannot read judge monitoring data.");}
    try{await new JudgeMonitorStore(factory,guestActor).GetAsync();throw new Exception("Guest accessed judge monitor.");}catch(UnauthorizedAccessException){Check(true,"Guest cannot read judge monitoring data.");}
    await using(var db=await factory.CreateDbContextAsync()){
        db.JudgeWorkers.AddRange(new JudgeWorkerEntity{Id="fresh-check",LastSeen=DateTimeOffset.UtcNow,State="Idle",Image="test"},new JudgeWorkerEntity{Id="stale-check",LastSeen=DateTimeOffset.UtcNow.AddMinutes(-1),State="Busy",Image="test"});await db.SaveChangesAsync();
    }
    var monitor=new JudgeMonitorStore(factory,adminActor);
    var workerStatus=await monitor.GetAsync();
    Check(workerStatus.Workers.Single(w=>w.Id=="fresh-check").Online&&!workerStatus.Workers.Single(w=>w.Id=="stale-check").Online,"Monitoring distinguishes fresh and stale Worker heartbeats.");
    Check((await accounts.GetAllAsync()).Length==2,"Administrator can create and list participant accounts.");
    await using(var db=await factory.CreateDbContextAsync()){
        var stored=await db.Users.AsNoTracking().SingleAsync(u=>u.Id==player.Id);
        Check(stored.PasswordHash!=participantPassword && stored.PasswordHash is not null,"Database stores a password hash, not the password.");
    }
    var adminPage=await admin.GetStringAsync("/admin");
    Check(WebUtility.HtmlDecode(adminPage).Contains("Панель администратора"),"Admin dashboard renders on the server before JavaScript starts.");
    var scriptUrl=Regex.Match(adminPage,"<script src=\"([^\"]*blazor\\.web[^\"]*)\"").Groups[1].Value;
    var script=await guest.GetAsync(scriptUrl);
    Check(script.IsSuccessStatusCode && script.Content.Headers.ContentType?.MediaType?.Contains("javascript")==true,"Blazor bootstrap script is public JavaScript, not login HTML.");
    var initialContestId=Guid.NewGuid();
    await using(var db=await factory.CreateDbContextAsync()){
        db.Contests.Add(new ContestEntity{Id=initialContestId,TitleRu="Initial access",TitleEn="Initial access",ParticipantIds=[player.Id],ProblemIdsJson=JsonSerializer.Serialize(DemoCatalog.Problems.Select(p=>p.Id)),StartsAt=DateTimeOffset.UtcNow.AddMinutes(-1),EndsAt=DateTimeOffset.UtcNow.AddHours(1),CreatedAt=DateTimeOffset.UtcNow,ParticipantLimit=100,DurationMinutes=60});await db.SaveChangesAsync();
    }
    var adminChat=new ChatService(factory,adminActor);
    var memberChat=new ChatService(factory,memberActor);
    await memberChat.SendAsync("Private participant question");
    await adminChat.SendAsync("Organizer reply",player.Id);
    Check((await memberChat.ReadAsync()).Length==2,"Participant and administrator messages persist in their conversation.");
    try{await memberChat.ReadAsync(adminUser.Id);throw new Exception("Chat isolation failed.");}catch(UnauthorizedAccessException){Check(true,"Participants cannot read another conversation.");}
    try{await new ChatService(factory,guestActor).SendAsync("guest");throw new Exception("Guest chat allowed.");}catch(UnauthorizedAccessException){Check(true,"Guest cannot send messages.");}
    var memberLogin=await PostForm(participant,"/account/login","/account/login",new(){{"Login","player_checks"},{"Password",participantPassword}});
    Check(memberLogin.StatusCode==HttpStatusCode.Redirect,"Participant can sign in.");
    Check((await participant.GetAsync("/admin/problems/new")).StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden,"Participant cannot access the problem authoring route.");
    var participantPage=WebUtility.HtmlDecode(await participant.GetStringAsync("/problems"));
    Check(participantPage.Contains("/chat") && !participantPage.Contains("href=\"/admin"),"Participant navigation includes chat and excludes admin links.");
    Check((await participant.GetAsync("/admin")).StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden,"Participant cannot open the admin dashboard directly.");
    Check((await participant.GetAsync("/admin/judge")).StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden,"Participant cannot open the judge monitor route.");
    var problemStore=new ProblemStore(factory,adminActor);
    var contestStore=new ContestStore(factory,adminActor);
    var memberProblems=new ProblemStore(factory,memberActor);
    Check((await memberProblems.GetAllAsync()).All(p=>p.Difficulty==""),"Participant problem data excludes difficulty.");
    Check((await problemStore.GetAllAsync()).Any(p=>p.Difficulty=="easy"),"Administrators retain difficulty metadata.");
    Check(!participantPage.Contains("class=\"difficulty ")&&!participantPage.Contains("<select"),"Participant library has no difficulty badges or filter.");
    var participantStatement=await participant.GetStringAsync("/problems/sum");
    Check(!participantStatement.Contains("class=\"eyebrow\""),"Participant problem statement does not display difficulty.");
    Check(DemoCatalog.Number(0)=="1"&&DemoCatalog.Number(25)=="26"&&DemoCatalog.Number(26)=="27","Problem numbering uses decimal numbers beyond 26 tasks.");
    var guestProblems=new ProblemStore(factory,guestActor);
    var memberContests=new ContestStore(factory,memberActor);
    var imported=(await contestStore.GetAllAsync()).Single(c=>c.Id==legacy.Id);
    Check(imported.Title.Ru==legacy.Title.Ru && File.Exists(Path.Combine(folder,"contests.json")),"Legacy draft is imported while the original JSON is preserved.");
    var pdf=Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\n%%EOF");
    var draft=new ProblemDraft{TitleRu="Тестовая сумма",TitleEn="Test sum",StatementRu="<script>not executable</script>\nНайдите сумму.",
        StatementEn="Find the sum.",TimeMs=1500,MemoryMb=128,SampleInput="2 3",SampleOutput="5",PdfRu=pdf};
    var problem=await problemStore.CreateAsync(draft);
    Check(SolutionFile.Read("SUM.PY",Encoding.UTF8.GetBytes("\uFEFFprint('привет')\n"))==new SolutionFile("Python","print('привет')\n"),"Source upload detects language, preserves Unicode and removes UTF-8 BOM.");
    foreach(var invalid in new[]{("x.exe",new byte[]{65}),("x.py",new byte[]{255}),("x.cpp",new byte[65537]),("x.py",new byte[]{0}),("x.py",Array.Empty<byte>())}){
        try{SolutionFile.Read(invalid.Item1,invalid.Item2);throw new Exception("Invalid source upload accepted.");}catch(DomainException){}
    }
    Check(true,"Source upload rejects binary, oversized, empty and unsupported files.");
    var suites=new TestSuiteStore(factory,adminActor);
    var txt="  2 3\r\n\r\n";
    Check(TestSuiteStore.ParseText("input.TXT",[..Encoding.UTF8.GetPreamble(),..Encoding.UTF8.GetBytes(txt)])==txt,"TXT import preserves whitespace and CRLF and removes UTF-8 BOM.");
    Check(TestSuiteStore.ParseText("empty.txt",[])=="","Empty TXT is a valid test field.");
    foreach(var invalid in new[]{("input.bin",Encoding.UTF8.GetBytes("1")),("input.txt",new byte[]{255,254,49,0}),("input.txt",Encoding.UTF8.GetBytes("a\0b")),("input.txt",new byte[TestSuiteStore.MaxTextBytes+1])}){
        try{TestSuiteStore.ParseText(invalid.Item1,invalid.Item2);throw new Exception("Invalid TXT accepted.");}catch(DomainException){}
    }
    Check(true,"TXT import rejects wrong extension, invalid UTF-8, null bytes and oversized files.");
    var creationDraft=new ProblemDraft{TitleRu="Создание с тестами",StatementRu="Примеры находятся в условии.",Tests=[new(){Input=txt,Output="5\r\n"},new(){Input="",Output="0"}]};
    var createdWithTests=await problemStore.CreateAsync(creationDraft);
    var createdSuite=await suites.GetAsync(createdWithTests.Id);
    Check(createdSuite.Revision==1&&createdSuite.Tests.Length==2&&createdSuite.Tests[0].Input==txt&&createdSuite.Tests[1].Input=="","Problem creation persists multiple hidden tests with exact text.");
    Check(createdWithTests.SampleInput==""&&createdWithTests.SampleOutput=="","Hidden tests do not become public examples.");
    var beforeInvalid=(await problemStore.GetAllAsync()).Length;
    creationDraft.Tests[0].Output=new string('x',TestSuiteStore.MaxTextBytes+1);
    try{await problemStore.CreateAsync(creationDraft);throw new Exception("Invalid creation tests accepted.");}catch(DomainException){}
    Check((await problemStore.GetAllAsync()).Length==beforeInvalid,"Invalid tests reject the whole problem creation without partial data.");
    var firstRevision=await suites.SaveAsync(problem.Id,0,[new(){Input="2 3\n",Output="5\n"},new(){Input="",Output=""}]);
    var savedSuite=await suites.GetAsync(problem.Id);
    Check(firstRevision==1 && savedSuite.Tests[0].Input=="2 3\n" && savedSuite.Tests.Length==2,"Hidden test suite preserves whitespace and empty inputs.");
    try{await suites.SaveAsync(problem.Id,0,[]);throw new Exception("Lost test update accepted.");}catch(DomainException){Check(true,"Stale test suite writes are rejected.");}
    try{await new TestSuiteStore(factory,memberActor).GetAsync(problem.Id);throw new Exception("Participant read tests.");}catch(UnauthorizedAccessException){Check(true,"Participants cannot read hidden tests from the service.");}
    try{await new TestSuiteStore(factory,memberActor).SaveAsync(problem.Id,1,[]);throw new Exception("Participant wrote tests.");}catch(UnauthorizedAccessException){Check(true,"Participants cannot change hidden tests.");}
    Check((await participant.GetAsync($"/admin/problems/{problem.Id}/tests")).StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden,"Hidden test page rejects participant requests.");
    foreach(var invalidJson in new[]{"null","[null]","[{}]","[{\"Input\":null,\"Output\":\"\"}]","[{\"Input\":\"\",\"Output\":\"\",\"Unknown\":1}]"}) {
        try{TestSuiteStore.Parse(invalidJson);throw new Exception("Invalid test import accepted.");}catch(DomainException){}
    }
    Check(true,"Malformed, missing and unknown test fields are rejected.");
    try{TestSuiteStore.Validate(Enumerable.Range(0,101).Select(_=>new TestCaseDraft()).ToArray());throw new Exception("Too many tests accepted.");}catch(DomainException){Check(true,"Test count limit is enforced.");}
    Check((await new ProblemStore(factory,adminActor).GetAllAsync()).Single(p=>p.Id==problem.Id).TimeMs==1500,"Problem statement, limits and examples persist in PostgreSQL.");
    Check(!(await memberProblems.GetAllAsync()).Any(p=>p.Id==problem.Id)&&!(await guestProblems.GetAllAsync()).Any(p=>p.Id==problem.Id),"Unpublished problems are hidden from participants and guests.");
    Check((await participant.GetAsync($"/problems/{problem.Id}/pdf/ru")).StatusCode==HttpStatusCode.NotFound,"PDF endpoint does not expose an unpublished problem.");
    var pdfResponse=await admin.GetAsync($"/problems/{problem.Id}/pdf/ru");
    Check(pdfResponse.IsSuccessStatusCode&&(await pdfResponse.Content.ReadAsByteArrayAsync()).SequenceEqual(pdf)&&pdfResponse.Content.Headers.ContentDisposition?.DispositionType=="attachment","Administrator can download the exact PDF as an attachment.");
    try{await memberProblems.CreateAsync(draft);throw new Exception("Unauthorized mutation accepted.");}catch(UnauthorizedAccessException){Check(true,"Service layer independently rejects participant problem creation.");}
    var bad=new ProblemDraft{TitleRu="x",PdfRu=Encoding.UTF8.GetBytes("<html>bad")};
    Check(bad.Validate().Contains("validation.pdf"),"Files without a PDF signature are rejected.");
    var oversized=new ProblemDraft{TitleRu="x",StatementRu="text",PdfRu=new byte[ProblemDraft.MaxPdfBytes+1]};
    Check(oversized.Validate().Contains("validation.pdf"),"Oversize PDFs are rejected.");
    // Checkers, test weights and groups, ZIP import, problem editing.
    Check(AnswerChecker.FloatTokensEqual("0.3333333333 x","0.333333333 x",1e-6)&&!AnswerChecker.FloatTokensEqual("0.3334","0.333333333",1e-6)&&AnswerChecker.FloatTokensEqual("1000000.5","1000000",1e-6)&&!AnswerChecker.FloatTokensEqual("NaN","nan",1)&&!AnswerChecker.FloatTokensEqual("1 2","1",1),"Float checker compares numbers with absolute or relative tolerance.");
    Check(AnswerChecker.LinesEqual("a b  \r\nc\n\n","a b\nc")&&!AnswerChecker.LinesEqual("a  b","a b")&&!AnswerChecker.LinesEqual("a\nb","a b")&&AnswerChecker.TokensEqual("a\nb","a b"),"Line checker keeps line structure and ignores trailing whitespace.");
    TestCaseDraft[] weighted=[new(){Points=0},new(){Points=30,Group="g"},new(){Points=30,Group="g"},new(){Points=40}];
    Check(TestScoring.Score(weighted,[true,true,false,true])==40&&TestScoring.Score(weighted,[false,true,true,true])==100&&TestScoring.Score([new(),new(),new()],[true,true,false])==66,"Weighted scores count groups only when all their tests pass.");
    foreach(var invalidPoints in new TestCaseDraft[][]{[new(){Points=-1}],[new(){Points=1001}],[new(){Points=0}],[new(){Group="bad group"}]}){
        try{TestSuiteStore.Validate(invalidPoints);throw new Exception("Invalid weights accepted.");}catch(DomainException){}
    }
    Check(TestSuiteStore.Parse("[{\"Input\":\"1\",\"Output\":\"1\",\"Points\":5,\"Group\":\"g1\"}]")[0].Points==5,"Invalid weights and groups are rejected; JSON import accepts Points and Group.");
    byte[] Zip(params (string Name,string Text)[] files){using var memory=new MemoryStream();using(var zip=new System.IO.Compression.ZipArchive(memory,System.IO.Compression.ZipArchiveMode.Create,true))foreach(var file in files){using var writer=new StreamWriter(zip.CreateEntry(file.Name).Open());writer.Write(file.Text);}return memory.ToArray();}
    var zipped=TestSuiteStore.ParseZip(Zip(("tests/10.in","ten"),("tests/2.in","two"),("tests/2.out","2"),("tests/10.ans","10"),("tests/readme.md","ignored")));
    Check(zipped.Length==2&&zipped[0].Input=="two"&&zipped[1].Output=="10","ZIP import pairs .in/.out/.ans in natural order and ignores other files.");
    Check(TestSuiteStore.ParseZip(Zip(("01","a"),("01.a","b"),("02","c"),("02.a","d"))).Length==2&&TestSuiteStore.ParseZip(Zip(("input/1.txt","x"),("output/1.txt","y"))).Single().Output=="y"&&TestSuiteStore.ParseZip(Zip(("input7.txt","x"),("output7.txt","y"))).Single().Input=="x","ZIP import supports Polygon names, input/output folders and inputN/outputN files.");
    foreach(var broken in new[]{Zip(("1.in","a")),Zip(("1.in","a"),("1","b"),("1.out","c")),Zip(("1.in",new string('x',TestSuiteStore.MaxTextBytes+1)),("1.out","c")),new byte[]{1,2,3}}){
        try{TestSuiteStore.ParseZip(broken);throw new Exception("Broken archive accepted.");}catch(DomainException){}
    }
    Check(true,"Unpaired, duplicate, oversized and corrupt archives are rejected.");
    Check(new ProblemDraft{TitleRu="x",StatementRu="y",CheckerMode=CheckerModes.Testlib}.Validate().Contains("checker.sourceInvalid")&&new ProblemDraft{TitleRu="x",StatementRu="y",CheckerMode=CheckerModes.Float,CheckerEpsilon=0}.Validate().Contains("checker.epsilonInvalid")&&new ProblemDraft{TitleRu="x",StatementRu="y",CheckerMode="shell"}.Validate().Contains("checker.invalidMode"),"Checker settings are validated.");
    {
        var adminProblems=new ProblemStore(factory,adminActor);
        var editable=await adminProblems.CreateAsync(new ProblemDraft{TitleRu="Редактируемая",StatementRu="text",Tests=[new(){Input="1\n",Output="1\n"}]});
        var pendingContest=await contestStore.CreateAsync(new(){Title="Scheduled snapshot check",Languages=["Python"],ProblemIds=[editable.Id],DurationMinutes=60,ParticipantLimit=10,StartsAt=DateTimeOffset.UtcNow.AddDays(1)});
        var edit=await adminProblems.GetDraftAsync(editable.Id);
        edit.TimeMs=2500;edit.CheckerMode=CheckerModes.Float;edit.CheckerEpsilon=1e-4;edit.SampleInput="1";
        await adminProblems.UpdateAsync(editable.Id,edit);
        var edited=(await adminProblems.GetAllAsync()).Single(p=>p.Id==editable.Id);
        Check(edited.TimeMs==2500&&edited.CheckerMode==CheckerModes.Float&&edited.CheckerEpsilon==1e-4&&edited.SampleInput=="1","Administrators edit limits, samples and checker settings.");
        try{await memberProblems.UpdateAsync(editable.Id,edit);throw new Exception("Participant edited a problem.");}catch(UnauthorizedAccessException){Check(true,"Participants cannot edit problems.");}
        Check((await participant.GetAsync($"/admin/problems/{editable.Id}/edit")).StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden,"Problem edit page rejects participant requests.");
        await suites.SaveAsync(editable.Id,1,[new(){Input="2\n",Output="2\n",Points=3},new(){Input="3\n",Output="3\n",Points=0}]);
        await using(var db=await factory.CreateDbContextAsync()){
            var snapshot=JsonSerializer.Deserialize<JudgeProblem[]>((await db.Contests.SingleAsync(c=>c.Id==pendingContest.Id)).JudgeProblemsJson)!.Single();
            Check(snapshot.Tests.Length==2&&snapshot.Tests[0].Points==3&&snapshot.TimeMs==2500&&snapshot.Checker?.Mode==CheckerModes.Float&&snapshot.Checker.Epsilon==1e-4,"Scheduled contests pick up edited tests, limits and checker settings before they start.");
            await db.Contests.Where(c=>c.Id==pendingContest.Id).ExecuteDeleteAsync();
        }
        try{await suites.SaveAsync(editable.Id,2,[new(){Input="1",Output="1",Points=0}]);throw new Exception("Zero-point suite accepted.");}catch(DomainException){Check(true,"A suite worth zero points is rejected.");}
        await adminProblems.DeleteAsync(editable.Id);
    }
    var contest=await contestStore.CreateAsync(new(){Title="Completed check contest",Scoring=ScoringMode.Points,Languages=["Python"],ProblemIds=[problem.Id],DurationMinutes=90,ParticipantLimit=75});
    Check(!(await memberContests.GetAllAsync()).Any(c=>c.Id==contest.Id),"Draft contests are hidden from participants.");
    Check(!(await contestStore.GetCompletedAsync()).Any(c=>c.Id==contest.Id),"Drafts do not appear in completed-contest results.");
    await using(var db=await factory.CreateDbContextAsync()){await db.Contests.Where(c=>c.Id==initialContestId).ExecuteDeleteAsync();}
    await contestStore.SetParticipantsAsync(contest.Id,[player.Id]);
    await contestStore.StartAsync(contest.Id);
    var submissions=new SubmissionStore(factory,memberActor);
    var submissionId=Guid.NewGuid();
    await submissions.SubmitAsync(submissionId,problem.Id,contest.Id,"Python","print(5)");
    Check(await submissions.SubmitAsync(submissionId,problem.Id,contest.Id,"Python","print(5)")==submissionId,"Submission retries are idempotent.");
    await suites.SaveAsync(problem.Id,1,[new(){Input="1 1",Output="2"}]);
    await using(var db=await factory.CreateDbContextAsync()){
        var saved=await db.Submissions.SingleAsync(s=>s.Id==submissionId);
        Check(JsonSerializer.Deserialize<JudgeProblem>(saved.SnapshotJson)!.Tests.Length==2,"Contest submissions retain the test snapshot captured at contest start.");
    }
    Check((await new SubmissionStore(factory,adminActor).HistoryAsync(problem.Id)).Length==0,"Submission history is private to its author.");
    try{await submissions.SubmitAsync(Guid.NewGuid(),problem.Id,null,"Python","print(5)");throw new Exception("Practice allowed for participant.");}catch(DomainException){Check(true,"Participants must select a running contest.");}
    try{await submissions.SubmitAsync(Guid.NewGuid(),problem.Id,contest.Id,"C++","int main(){}");throw new Exception("Disabled language accepted.");}catch(DomainException){Check(true,"Contest language restrictions are enforced.");}
    for(var i=0;i<2;i++)await submissions.SubmitAsync(Guid.NewGuid(),problem.Id,contest.Id,"Python","print(5)");
    try{await submissions.SubmitAsync(Guid.NewGuid(),problem.Id,contest.Id,"Python","print(5)");throw new Exception("Queue limit bypassed.");}catch(DomainException){Check(true,"Pending submission limit is enforced.");}
    var queuedMonitor=await monitor.GetAsync("Queued","Python",contest.Id);
    Check(queuedMonitor.Queued==3&&queuedMonitor.Items.Length==3&&queuedMonitor.Items.All(s=>s.ContestId==contest.Id&&s.Language=="Python"),"Admin queue filters show matching submissions and global counts.");
    Check((await monitor.GetAsync("Accepted","Java",contest.Id)).Items.Length==0,"Admin queue filters exclude unrelated submissions.");
    if(args.Contains("--judge")){
        await using(var db=await factory.CreateDbContextAsync()){var abandoned=await db.Submissions.SingleAsync(s=>s.Id==submissionId);abandoned.Status="Running";abandoned.LeaseUntil=DateTimeOffset.UtcNow.AddMinutes(-2);abandoned.LeaseOwner="expired-test";await db.SaveChangesAsync();}
        var workerStart=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        workerStart.ArgumentList.Add(Path.Combine(root,"src","BeastContest.Judge","bin","Debug","net10.0","BeastContest.Judge.dll"));workerStart.Environment["ConnectionStrings__Database"]=connection;
        using var worker=Process.Start(workerStart)!;var workerOut=worker.StandardOutput.ReadToEndAsync();var workerErr=worker.StandardError.ReadToEndAsync();
        try{
            SubmissionSummary[] judged=[];
            for(var attempt=0;attempt<120;attempt++){judged=await submissions.HistoryAsync(problem.Id);if(judged.All(s=>s.Status is not ("Queued" or "Running")))break;await Task.Delay(1000);}
            Check(judged.Length==3 && judged.All(s=>s.Status=="WrongAnswer" && s.Passed==1),"Worker drains the database queue, recovers an expired lease and stores real verdicts.");
            var liveRejudge=new RejudgeStore(factory,adminActor);
            await suites.SaveAsync(problem.Id,2,[new(){Input="2 3",Output="5"}]);
            await liveRejudge.RunAsync(Guid.NewGuid(),null,contest.Id,true,"Corrected expected output");
            for(var attempt=0;attempt<120;attempt++){judged=await submissions.HistoryAsync(problem.Id);if(judged.All(s=>s.Status is not ("Queued" or "Running")))break;await Task.Delay(1000);}
            Check(judged.Length==3&&judged.All(s=>s.Status=="Accepted"&&s.Passed==1&&s.Total==1),"Real Worker rejudges the entire contest using updated tests.");
            Check((await monitor.GetAsync()).Workers.Any(w=>w.Online&&w.Id is not ("fresh-check" or "stale-check")),"The real Worker publishes heartbeat data independently of submissions.");
        }finally{if(!worker.HasExited)worker.Kill(true);await worker.WaitForExitAsync();await File.WriteAllTextAsync(Path.Combine(folder,"worker.log"),await workerOut+await workerErr);}
    }
    Check((await memberProblems.GetAllAsync()).Any(p=>p.Id==problem.Id),"Starting a contest makes its problems visible to signed-in participants.");
    Check((await participant.GetAsync($"/problems/{problem.Id}/pdf/ru")).IsSuccessStatusCode,"Participant can download a published PDF.");
    Check(!(await contestStore.GetCompletedAsync()).Any(c=>c.Id==contest.Id),"Running contests do not appear in completed-contest results.");
    Check((await new StandingsStore(factory,memberActor,memberContests).GetViewAsync(contest.Id)).Rows.Length==1,"Started contests expose a live scoreboard to participants.");
    var runningScores=new StandingsStore(factory,adminActor,contestStore);
    await runningScores.SetFrozenAsync(contest.Id,true);
    await contestStore.FinishAsync(contest.Id);
    Check((await new StandingsStore(factory,memberActor,memberContests).GetViewAsync(contest.Id)).FrozenAt!=null,"Finishing a contest does not automatically publish frozen results.");
    await runningScores.SetFrozenAsync(contest.Id,false);
    try{await runningScores.SetFrozenAsync(legacy.Id,true);throw new Exception("Draft frozen.");}catch(DomainException){Check(true,"Draft contests cannot be frozen.");}
    try{await runningScores.SetFrozenAsync(DemoCatalog.Contests[0].Id,true);throw new Exception("Demo frozen.");}catch(DomainException){Check(true,"Demo contests cannot be frozen.");}
    var scoreContest=contest with{Scoring=ScoringMode.Icpc,StartsAt=DateTimeOffset.UtcNow.AddMinutes(-60)};
    SubmissionEntity Attempt(string user,string status,int minute,int passed=0)=>new(){Id=Guid.NewGuid(),ContestId=contest.Id,ProblemId=problem.Id,UserId=user,UserName=user,Status=status,CreatedAt=scoreContest.StartsAt!.Value.AddMinutes(minute),Passed=passed,Total=2,Score=passed*50};
    var scoreAttempts=new[]{Attempt("A","WrongAnswer",1,1),Attempt("A","CompilationError",2),Attempt("A","Accepted",10,2),Attempt("B","Accepted",30,2)};
    var icpc=StandingsStore.Calculate(scoreContest,scoreAttempts);
    Check(icpc.Length==2&&icpc.All(r=>r.Score==1&&r.Penalty==30&&r.Rank==1),"ICPC ignores compilation errors, applies 20-minute penalties and shares tied ranks.");
    var points=StandingsStore.Calculate(scoreContest with{Scoring=ScoringMode.Points},scoreAttempts);
    Check(points.All(r=>r.Score==100)&&points[0].Name=="A"&&points[0].Rank==1&&points[1].Rank==2,"Equal point totals are ordered by the time they were reached.");
    Check(points[0].TimeSeconds==600&&points[0].Attempts==3&&points[0].ProblemSeconds![0]==600&&points[0].ProblemAttempts![0]==3,"Standings include elapsed time and all attempts including compilation errors.");
    var tieAttempts=new[]{Attempt("Early","WrongAnswer",5,1),Attempt("Early","WrongAnswer",40,1),Attempt("Late","WrongAnswer",20,1),Attempt("SameTime","WrongAnswer",5,1),Attempt("Early","Queued",50)};
    var tied=StandingsStore.Calculate(scoreContest with{Scoring=ScoringMode.Points},tieAttempts.Reverse());
    Check(tied.Single(r=>r.Name=="Early").Rank==1&&tied.Single(r=>r.Name=="SameTime").Rank==1&&tied.Single(r=>r.Name=="Late").Rank==3,"First best partial score determines time; repeated scores and pending attempts do not worsen it.");
    Check(tied.Single(r=>r.Name=="Early").Attempts==3,"All attempts remain visible after reaching the best score.");
    var secondProblem=Attempt("Early","Accepted",25,2);secondProblem.ProblemId="another";
    var multi=StandingsStore.Calculate(scoreContest with{Scoring=ScoringMode.Points,ProblemIds=[problem.Id,"another"]},tieAttempts.Append(secondProblem));
    Check(multi[0].Score==150&&multi[0].TimeSeconds==1500&&multi[0].ProblemSeconds!.SequenceEqual(new long?[]{300,1500}),"Total attainment time is the latest of the first best times across problems.");
    var zero=StandingsStore.Calculate(scoreContest with{Scoring=ScoringMode.Points},new[]{Attempt("Zero","WrongAnswer",1),Attempt("Pending","Queued",2)});
    Check(zero.All(r=>r.Score==0&&r.TimeSeconds==null&&r.Rank==1),"Zero-score and pending-only participants have no achievement time and share rank.");
    var legacyRow=JsonSerializer.Deserialize<StandingRow>("{\"Rank\":1,\"Name\":\"Legacy\",\"Score\":100,\"Penalty\":0,\"Results\":[\"100\"]}")!;
    Check(legacyRow.TimeSeconds is null&&legacyRow.Attempts is null&&StandingsStore.FormatTime(90061)=="25:01:01","Older frozen rows remain readable and durations can exceed 24 hours.");
    Check((await new StandingsStore(factory,memberActor,memberContests).GetAsync(contest.Id)).Length==1,"Completed-contest standings read only that contest's submissions.");
    var adminScores=new StandingsStore(factory,adminActor,contestStore);
    var memberScores=new StandingsStore(factory,memberActor,memberContests);
    foreach(var deniedActor in new[]{memberActor,new FixedActor(Actor.Guest)}){
        try{await new StandingsStore(factory,deniedActor,contestStore).SetFrozenAsync(contest.Id,true);throw new Exception("Unauthorized freeze.");}catch(UnauthorizedAccessException){Check(true,"Only admins may freeze standings.");}
        try{await new StandingsStore(factory,deniedActor,contestStore).SetFrozenAsync(contest.Id,false);throw new Exception("Unauthorized publish.");}catch(UnauthorizedAccessException){Check(true,"Only admins may publish standings.");}
    }
    try{await new StandingsStore(factory,new FixedActor(Actor.Guest),contestStore).GetViewAsync(contest.Id);throw new Exception("Guest read standings.");}catch(UnauthorizedAccessException){Check(true,"Guests cannot retrieve standings snapshots.");}
    var csvUrl=$"/contests/{contest.Id}/standings.csv";
    Check(!(await guest.GetAsync(csvUrl)).IsSuccessStatusCode,"CSV download requires authentication.");
    Check((await admin.GetAsync($"/contests/{legacy.Id}/standings.csv")).StatusCode==HttpStatusCode.NotFound,"CSV does not expose draft contests.");
    Check((await admin.GetAsync($"/contests/{Guid.NewGuid()}/standings.csv")).StatusCode==HttpStatusCode.NotFound,"Unknown contests have no CSV export.");
    Check((await admin.GetAsync($"/contests/{DemoCatalog.Contests[0].Id}/standings.csv")).StatusCode==HttpStatusCode.NotFound,"Demo exports are not presented as real results.");
    Check((await admin.GetAsync(csvUrl+"?lang=xx")).StatusCode==HttpStatusCode.BadRequest,"Unsupported CSV locales are rejected.");
    var csvName=" =2+2;\"Имя\"\r\nстрока";
    await using(var db=await factory.CreateDbContextAsync()){await db.Submissions.Where(j=>j.ContestId==contest.Id).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.UserName,csvName));}
    var beforeFreeze=await memberScores.GetAsync(contest.Id);
    await adminScores.SetFrozenAsync(contest.Id,true);
    var firstFreeze=await memberScores.GetViewAsync(contest.Id);
    var frozenCsv=await participant.GetByteArrayAsync(csvUrl+"?lang=ru");
    Check(frozenCsv.Take(3).SequenceEqual(new byte[]{239,187,191}),"CSV includes a UTF-8 BOM for Cyrillic spreadsheet users.");
    string[][] CsvRows(byte[] bytes){
        using var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')));
        parser.SetDelimiters(";");parser.HasFieldsEnclosedInQuotes=true;parser.TrimWhiteSpace=false;
        var rows=new List<string[]>();while(!parser.EndOfData)rows.Add(parser.ReadFields()!);return rows.ToArray();
    }
    var frozenCells=CsvRows(frozenCsv);
    Check(frozenCells[0][9]=="1"&&frozenCells[0][10]=="1 · Время","CSV problem headers use the same numeric numbering as standings.");
    Check(frozenCells.Length==2&&frozenCells.All(r=>r.Length==12)&&frozenCells[1][5]=="'"+csvName,"CSV round-trips semicolons, quotes, Unicode and newlines, while neutralizing formulas.");
    Check(frozenCells[0][7]=="Время"&&frozenCells[0][8]=="Попытки"&&frozenCells[1][8]==beforeFreeze[0].Attempts.ToString(),"CSV exports the same time and attempts as the frozen standings.");
    Check(frozenCells[0][0]=="Контест"&&frozenCells[1][2]=="Замороженная"&&frozenCells[1][3].Length>0,"Participant CSV labels frozen results and their snapshot time.");
    using(var response=await participant.GetAsync(csvUrl+"?lang=en")){
        Check(response.Content.Headers.ContentType?.MediaType=="text/csv"&&response.Content.Headers.ContentDisposition?.DispositionType=="attachment"&&response.Headers.CacheControl?.NoStore==true,"CSV is an attachment with private no-store caching.");
        var english=CsvRows(await response.Content.ReadAsByteArrayAsync());
        Check(english[0][0]=="Contest"&&english[0][5]=="Participant"&&english[1][2]=="Frozen","English CSV localizes headers and visibility metadata.");
    }
    Check(firstFreeze.FrozenAt!=null&&!firstFreeze.IsAdmin&&JsonSerializer.Serialize(firstFreeze.Rows)==JsonSerializer.Serialize(beforeFreeze),"Freeze captures the exact current standings.");
    await using(var db=await factory.CreateDbContextAsync()){
        var changed=await db.Submissions.Where(j=>j.ContestId==contest.Id).ToArrayAsync();
        foreach(var job in changed){job.Status=beforeFreeze[0].Score==0?"Accepted":"WrongAnswer";job.Passed=beforeFreeze[0].Score==0?1:0;job.Total=1;job.Score=job.Passed*100;}
        await db.SaveChangesAsync();
    }
    await adminScores.SetFrozenAsync(contest.Id,true);
    Check((await memberScores.GetViewAsync(contest.Id)).FrozenAt==firstFreeze.FrozenAt&&JsonSerializer.Serialize(await memberScores.GetAsync(contest.Id))==JsonSerializer.Serialize(beforeFreeze),"Repeated freeze preserves the snapshot despite later verdicts.");
    Check((await adminScores.GetAsync(contest.Id))[0].Score!=beforeFreeze[0].Score,"Admins see current results while participants see the frozen snapshot.");
    Check((await participant.GetByteArrayAsync(csvUrl+"?lang=ru")).SequenceEqual(frozenCsv),"CSV download cannot bypass the freeze after new verdicts.");
    var adminCsv=CsvRows(await admin.GetByteArrayAsync(csvUrl+"?lang=ru"));
    Check(adminCsv[1][2]=="Текущая"&&adminCsv[1][3]==""&&adminCsv[1][6]!=frozenCells[1][6],"Admin CSV contains current scores rather than the participant snapshot.");
    var rejudges=new RejudgeStore(factory,adminActor);
    foreach(var deniedActor in new[]{memberActor,new FixedActor(Actor.Guest)}){
        try{await new RejudgeStore(factory,deniedActor).RunAsync(Guid.NewGuid(),submissionId,null,false,"Unauthorized retry");throw new Exception("Rejudge access bypassed.");}catch(UnauthorizedAccessException){Check(true,"Non-admin cannot rejudge submissions.");}
        try{await new RejudgeStore(factory,deniedActor).HistoryAsync();throw new Exception("Audit access bypassed.");}catch(UnauthorizedAccessException){Check(true,"Non-admin cannot read rejudge history.");}
    }
    string originalSource,originalSnapshot;DateTimeOffset originalTime;
    await using(var db=await factory.CreateDbContextAsync()){
        var job=await db.Submissions.SingleAsync(s=>s.Id==submissionId);originalSource=job.Source;originalSnapshot=job.SnapshotJson;originalTime=job.CreatedAt;
        job.Status="Running";job.LeaseOwner="old-worker";job.LeaseUntil=DateTimeOffset.UtcNow.AddMinutes(1);job.Attempts=3;job.Passed=1;await db.SaveChangesAsync();
    }
    var retryId=Guid.NewGuid();
    var concurrentRetries=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>rejudges.RunAsync(retryId,submissionId,null,false,"Retry after worker failure")));
    Check(concurrentRetries.All(n=>n==1),"Concurrent retries of one rejudge request are idempotent.");
    await using(var db=await factory.CreateDbContextAsync()){
        var job=await db.Submissions.SingleAsync(s=>s.Id==submissionId);
        Check(job.Status=="Queued"&&job.LeaseOwner==null&&job.LeaseUntil==null&&job.Attempts==0&&job.Passed==0,"Rejudge resets attempts and invalidates active leases.");
        Check(job.Source==originalSource&&job.CreatedAt==originalTime&&job.SnapshotJson==originalSnapshot,"Single retry preserves source, original submission time and tests.");
        Check(await db.Submissions.Where(s=>s.Id==submissionId&&s.LeaseOwner=="old-worker"&&s.Status=="Running").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Accepted"))==0,"Stale Worker cannot overwrite a rejudged submission.");
        var audit=await db.Rejudges.SingleAsync(r=>r.Id==retryId);
        var prior=JsonSerializer.Deserialize<PreviousVerdict[]>(audit.PreviousJson)!;
        Check(prior.Length==1&&prior[0].Status=="Running"&&prior[0].SnapshotJson==originalSnapshot,"Audit retains previous verdict and test snapshot exactly once.");
    }
    try{await rejudges.RunAsync(Guid.NewGuid(),submissionId,null,true,"Selective new tests");throw new Exception("Unequal contest tests allowed.");}catch(DomainException){Check(true,"Updated tests cannot be applied selectively to one submission.");}
    var currentSuite=await suites.GetAsync(problem.Id);
    await suites.SaveAsync(problem.Id,currentSuite.Revision,[]);
    var failedId=Guid.NewGuid();
    try{await rejudges.RunAsync(failedId,null,contest.Id,true,"Empty tests must fail");throw new Exception("Empty tests accepted.");}catch(DomainException){Check(true,"Empty updated tests reject the whole rejudge.");}
    await using(var db=await factory.CreateDbContextAsync()){Check(!await db.Rejudges.AnyAsync(r=>r.Id==failedId)&&(await db.Submissions.SingleAsync(s=>s.Id==submissionId)).SnapshotJson==originalSnapshot,"Failed rejudge leaves no audit record or partially updated submission.");}
    await suites.SaveAsync(problem.Id,currentSuite.Revision+1,[new(){Input="3 2",Output="5"}]);
    Check(await rejudges.RunAsync(Guid.NewGuid(),null,contest.Id,true,"Apply corrected contest tests")==3,"Completed contests support rejudging every submission.");
    await using(var db=await factory.CreateDbContextAsync()){
        var all=await db.Submissions.Where(s=>s.ContestId==contest.Id).ToArrayAsync();
        var savedContest=await db.Contests.SingleAsync(c=>c.Id==contest.Id);
        var snapshot=JsonSerializer.Deserialize<JudgeProblem[]>(savedContest.JudgeProblemsJson)!.Single();
        Check(all.All(j=>JsonSerializer.Deserialize<JudgeProblem>(j.SnapshotJson)!.Revision==snapshot.Revision&&j.Total==1)&&snapshot.Tests.Single().Input=="3 2","Contest and all submission snapshots change atomically to the same revision.");
    }
    Check(JsonSerializer.Serialize(await memberScores.GetAsync(contest.Id))==JsonSerializer.Serialize(beforeFreeze),"Rejudge does not leak changed standings through the participant service.");
    Check((await participant.GetByteArrayAsync(csvUrl+"?lang=ru")).SequenceEqual(frozenCsv),"Rejudge does not leak changed results through CSV.");
    await adminScores.SetFrozenAsync(contest.Id,false);
    Check(CsvRows(await participant.GetByteArrayAsync(csvUrl+"?lang=ru"))[1][2]=="Текущая","Publishing releases current CSV results.");
    await using(var db=await factory.CreateDbContextAsync()){await db.Contests.Where(c=>c.Id==contest.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Scoring,(int)ScoringMode.Icpc));}
    var icpcCsv=CsvRows(await admin.GetByteArrayAsync(csvUrl+"?lang=en"));
    Check(icpcCsv.All(r=>r.Length==13)&&icpcCsv[0][7]=="Penalty","ICPC CSV includes penalty, time, attempts and problem columns.");
    await using(var db=await factory.CreateDbContextAsync()){await db.Contests.Where(c=>c.Id==contest.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Scoring,(int)ScoringMode.Points));}
    Check((await memberScores.GetViewAsync(contest.Id)).FrozenAt==null&&JsonSerializer.Serialize(await memberScores.GetAsync(contest.Id))==JsonSerializer.Serialize(await adminScores.GetAsync(contest.Id)),"Publishing releases current results to participants.");
    await adminScores.SetFrozenAsync(contest.Id,false);
    Check((await memberScores.GetViewAsync(contest.Id)).FrozenAt==null,"Publishing is idempotent.");
    Check((await contestStore.GetCompletedAsync()).Any(c=>c.Id==contest.Id),"Finished contest appears in completed-contest selector.");
    try{await contestStore.StartAsync(contest.Id);throw new Exception("Finished contest restarted.");}catch(DomainException){Check(true,"Invalid lifecycle transitions are rejected.");}
    var before=await contestStore.GetAllAsync();
    await Task.WhenAll(Enumerable.Range(0,10).Select(i=>contestStore.CreateAsync(new(){Title="Concurrent "+i,ProblemIds=["sum"]})));
    Check((await contestStore.GetAllAsync()).Length==before.Length+10,"Concurrent contest saves are not lost.");
    var currentState=new TestAuthState(new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.NameIdentifier,player.Id),new Claim("AspNet.Identity.SecurityStamp",player.SecurityStamp!)},"test")));
    var actualActor=new CurrentActor(currentState,provider.GetRequiredService<IServiceScopeFactory>());
    Check((await actualActor.GetAsync()).IsAuthenticated,"CurrentActor validates the database user and security stamp.");
    await accounts.SetDisabledAsync(player.Id,true);
    Check(!(await actualActor.GetAsync()).IsAuthenticated,"Disabled user immediately loses service-level authorization.");
    Check((await participant.GetAsync($"/problems/{problem.Id}/pdf/ru")).StatusCode==HttpStatusCode.Unauthorized,"Disabled user's existing cookie cannot download PDFs.");
    Check((await participant.GetAsync(csvUrl)).StatusCode==HttpStatusCode.Unauthorized,"Disabled account cannot download CSV using an existing cookie.");
    await accounts.SetDisabledAsync(player.Id,false);
    var resetPassword=AccountService.GenerateParticipantPassword();
    await accounts.ResetPasswordAsync(player.Id,resetPassword);
    Check((await participant.GetAsync(csvUrl)).StatusCode==HttpStatusCode.Unauthorized,"Password reset immediately revokes CSV downloads with the old cookie.");
    var oldLogin=await PostForm(guest,"/account/login","/account/login",new(){{"Login","player_checks"},{"Password",participantPassword}});
    Check(oldLogin.StatusCode==HttpStatusCode.OK&&WebUtility.HtmlDecode(await oldLogin.Content.ReadAsStringAsync()).Contains("Неверный"),"Old password is rejected after an administrator reset.");
    var newLogin=await PostForm(guest,"/account/login","/account/login",new(){{"Login","player_checks"},{"Password",resetPassword}});
    Check(newLogin.StatusCode==HttpStatusCode.Redirect,"New password works after reset.");
    var logout=await PostForm(admin,"/account/login","/account/logout",new());
    Check(logout.StatusCode==HttpStatusCode.Redirect,"Antiforgery-protected logout clears the session.");
    Check((await admin.GetAsync("/admin/users")).StatusCode==HttpStatusCode.Redirect,"Logged-out client loses access to admin routes.");
    var badContest=new ContestDraft{Title=" ",DurationMinutes=-1,ParticipantLimit=101,Scoring=(ScoringMode)99,Languages=["Unknown"]};
    Check(badContest.Validate().Length==5,"Invalid title, duration, participant limit, scoring, and languages are rejected.");
    foreach(var forbidden in new[]{memberProblems,guestProblems}){
        try{await forbidden.DeleteAsync(createdWithTests.Id);throw new Exception("Unauthorized deletion accepted.");}catch(UnauthorizedAccessException){Check(true,"Only an administrator can delete a problem.");}
    }
    try{await problemStore.DeleteAsync("sum");throw new Exception("Demo deleted.");}catch(DomainException ex){Check(ex.Keys.Contains("problem.deleteDemo"),"Demo deletion is rejected.");}
    try{await problemStore.DeleteAsync(problem.Id);throw new Exception("Used problem deleted.");}catch(DomainException ex){Check(ex.Keys.Contains("problem.deleteUsed"),"Finished contest problems and result history are protected.");}
    var disposable=await problemStore.CreateAsync(new(){TitleRu="Удаление с PDF",PdfRu=pdf,Tests=[new(){Input="1",Output="1"}]});
    await problemStore.DeleteAsync(disposable.Id);
    await using(var db=await factory.CreateDbContextAsync()){
        Check(!await db.Problems.AnyAsync(p=>p.Id==disposable.Id)&&!await db.ProblemTests.AnyAsync(p=>p.ProblemId==disposable.Id),"Deleting an unused problem removes its row, PDF and hidden tests.");
    }
    try{await problemStore.DeleteAsync(disposable.Id);throw new Exception("Missing deletion accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("common.notfound"),"Repeated deletion reports not found.");}
    try{await contestStore.CreateAsync(new(){Title="Removed problem",ProblemIds=[disposable.Id]});throw new Exception("Deleted problem used.");}catch(DomainException ex){Check(ex.Keys.Contains("validation.problems"),"A stale contest draft cannot reference a deleted problem.");}
    try{await suites.SaveAsync(disposable.Id,0,[]);throw new Exception("Deleted tests recreated.");}catch(DomainException ex){Check(ex.Keys.Contains("common.notfound"),"A stale test editor cannot recreate deleted tests.");}
    async Task<bool> DeleteOrUsed(string id){try{await problemStore.DeleteAsync(id);return true;}catch(DomainException ex) when(ex.Keys.Contains("problem.deleteUsed")){return false;}}
    async Task<bool> CreateOrMissing(string id){try{await contestStore.CreateAsync(new(){Title="Deletion race",ProblemIds=[id]});return true;}catch(DomainException ex) when(ex.Keys.Contains("validation.problems")){return false;}}
    for(var i=0;i<3;i++){
        var raceProblem=await problemStore.CreateAsync(new(){TitleRu="Параллельное удаление",StatementRu="test"});
        var deleteTask=DeleteOrUsed(raceProblem.Id);var createTask=CreateOrMissing(raceProblem.Id);
        await Task.WhenAll(deleteTask,createTask);
        Check(deleteTask.Result!=createTask.Result,"Concurrent delete/contest creation has exactly one winner.");
    }
    var draftProtected=await problemStore.CreateAsync(new(){TitleRu="Черновик",StatementRu="test"});
    await contestStore.CreateAsync(new(){Title="Protected draft",ProblemIds=[draftProtected.Id]});
    Check(!await DeleteOrUsed(draftProtected.Id),"Even a draft contest protects its problems.");
    var practiceProtected=await problemStore.CreateAsync(new(){TitleRu="Проверка админа",StatementRu="test",Tests=[new(){Input="",Output="5"}]});
    await new SubmissionStore(factory,adminActor).SubmitAsync(Guid.NewGuid(),practiceProtected.Id,null,"Python","print(5)");
    Check(!await DeleteOrUsed(practiceProtected.Id),"Practice submissions protect a problem without a contest.");
    var generated=await accounts.CreateParticipantAsync("generated_checks","Generated participant");
    Check(generated.Password.Length==6&&generated.Password.Distinct().Count()>=3,"Generated participant passwords contain exactly six characters.");
    var generatedUser=(await accounts.GetAllAsync()).Single(u=>u.Login==generated.Login);
    Check(generatedUser.Role=="Participant","Web account creation always assigns the participant role.");
    try{await accounts.CreateParticipantAsync(generated.Login,"Duplicate");throw new Exception("Duplicate accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("auth.duplicate"),"Duplicate logins do not overwrite accounts.");}
    foreach(var password in new[]{"abc12","abc1234"}){
        Check(AccountService.Validate("valid","Valid",password,"Participant").Contains("users.participantPasswordRules"),"Participant passwords reject lengths other than six.");
        try{await accounts.ResetPasswordAsync(generatedUser.Id,password);throw new Exception("Bad reset accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("users.participantPasswordRules"),"Participant reset enforces six-character limit.");}
    }
    Check(AccountService.Validate("admin2","Admin","Abc123","Admin").Contains("auth.passwordRules"),"Administrator creation still requires at least ten characters.");
    try{await accounts.ResetPasswordAsync((await adminActor.GetAsync()).Id,"Abc123");throw new Exception("Short admin reset accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("auth.passwordRules"),"Administrator reset still requires at least ten characters.");}
    foreach(var deniedActor in new[]{memberActor,guestActor}){
        var deniedAccounts=new AccountService(deniedActor,provider.GetRequiredService<IServiceScopeFactory>());
        try{await deniedAccounts.CreateParticipantAsync("denied","Denied");throw new Exception("Unauthorized creation.");}catch(UnauthorizedAccessException){Check(true,"Only an admin can create participants.");}
        try{await deniedAccounts.DeleteParticipantAsync(generatedUser.Id);throw new Exception("Unauthorized deletion.");}catch(UnauthorizedAccessException){Check(true,"Only an admin can delete participants.");}
    }
    var adminId=(await adminActor.GetAsync()).Id;
    try{await accounts.DeleteParticipantAsync(adminId);throw new Exception("Admin deleted.");}catch(DomainException ex){Check(ex.Keys.Contains("users.deleteAdmin"),"Administrator accounts cannot be deleted.");}
    int oldSubmissions,oldMessages;
    await using(var db=await factory.CreateDbContextAsync()){
        oldSubmissions=await db.Submissions.CountAsync(s=>s.UserId==player.Id);oldMessages=await db.ChatMessages.CountAsync(m=>m.ParticipantId==player.Id);
    }
    await accounts.DeleteParticipantAsync(player.Id);
    Check(!(await accounts.GetAllAsync()).Any(u=>u.Id==player.Id),"Deleted participants disappear from account management.");
    Check((await guest.GetAsync($"/problems/{problem.Id}/pdf/ru")).StatusCode==HttpStatusCode.Unauthorized,"A deleted participant's existing cookie loses access immediately.");
    await using(var db=await factory.CreateDbContextAsync()){
        Check(await db.Submissions.CountAsync(s=>s.UserId==player.Id)==oldSubmissions&&await db.ChatMessages.CountAsync(m=>m.ParticipantId==player.Id)==oldMessages,"Deleting accounts preserves submissions and chat history.");
    }
    var recreated=await accounts.CreateParticipantAsync("player_checks","New participant");
    Check((await accounts.GetAllAsync()).Single(u=>u.Login==recreated.Login).Id!=player.Id,"Reusing a login creates a new identity without inheriting results.");
    var progressUser=(await accounts.GetAllAsync()).Single(u=>u.Login==recreated.Login);
    var progressStore=new SubmissionStore(factory,new FixedActor(new(progressUser.Id,progressUser.DisplayName,false)));
    Check((await progressStore.ProblemStatusesAsync()).Count==0,"A new identity has no inherited problem progress.");
    var progressContestId=Guid.NewGuid();
    await using(var db=await factory.CreateDbContextAsync()){
        db.Contests.Add(new ContestEntity{Id=progressContestId,TitleRu="Текущий контест",TitleEn="Current contest",ParticipantIds=[progressUser.Id],ProblemIdsJson=JsonSerializer.Serialize(DemoCatalog.Problems.Select(p=>p.Id)),StartsAt=DateTimeOffset.UtcNow.AddMinutes(-5),EndsAt=DateTimeOffset.UtcNow.AddHours(1),CreatedAt=DateTimeOffset.UtcNow,ParticipantLimit=100,DurationMinutes=65});await db.SaveChangesAsync();
    }
    SubmissionEntity Progress(string id,string status,string? owner=null)=>new(){Id=Guid.NewGuid(),UserId=owner??progressUser.Id,ContestId=progressContestId,UserName=recreated.Login,ProblemId=id,Status=status,Language="Python",CreatedAt=DateTimeOffset.UtcNow,Total=2,Passed=status=="Accepted"?2:0};
    await using(var db=await factory.CreateDbContextAsync()){
        db.Submissions.AddRange(Progress("sum","Accepted"),Progress("route","WrongAnswer"),Progress("sequence","Running"),Progress("network","SystemError"),Progress("archive","Accepted",adminId));
        for(var i=0;i<35;i++)db.Submissions.Add(Progress("sum","WrongAnswer"));
        await db.SaveChangesAsync();
    }
    var progress=await progressStore.ProblemStatusesAsync();
    Check(progress["sum"]==PersonalProblemStatus.Accepted,"A solved problem stays green after more than 30 later failed attempts.");
    Check(progress["route"]==PersonalProblemStatus.Failed&&progress["sequence"]==PersonalProblemStatus.Pending,"Failed and running solutions have different personal states.");
    Check(progress["network"]==PersonalProblemStatus.SystemError&&!progress.ContainsKey("archive"),"Judge errors stay neutral and other users' results remain private.");
    try{await new SubmissionStore(factory,guestActor).ProblemStatusesAsync();throw new Exception("Guest read progress.");}catch(UnauthorizedAccessException){Check(true,"Guests cannot retrieve personal problem statuses.");}
    await using(var db=await factory.CreateDbContextAsync()){
        db.Submissions.Add(Progress("route","Queued"));
        await db.Submissions.Where(s=>s.UserId==progressUser.Id&&s.Status=="Accepted").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Running"));
        await db.SaveChangesAsync();
    }
    progress=await progressStore.ProblemStatusesAsync();
    Check(progress["route"]==PersonalProblemStatus.Pending&&progress["sum"]==PersonalProblemStatus.Pending,"Retries and rejudging update progress without retaining stale success.");
    await using(var db=await factory.CreateDbContextAsync()){
        await db.Submissions.Where(s=>s.UserId==progressUser.Id&&s.ProblemId=="sum"&&s.Status=="Running").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Accepted"));
        await db.Submissions.Where(s=>s.UserId==progressUser.Id&&s.ProblemId=="route"&&s.Status=="Queued").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"CompilationError"));
    }
    Check((await progressStore.ProblemStatusesAsync())["route"]==PersonalProblemStatus.Failed,"Compilation errors count as unsuccessful attempts.");
    var idleCredentials=await accounts.CreateParticipantAsync("idle_checks","Not enrolled");
    using var accessClient=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=admin.BaseAddress};
    await PostForm(accessClient,"/account/login","/account/login",new(){{"Login",idleCredentials.Login},{"Password",idleCredentials.Password}});
    var idleHtml=WebUtility.HtmlDecode(await accessClient.GetStringAsync("/problems"));
    Check(idleHtml.Contains("Вы не участвуете ни в одном контесте")&&idleHtml.Contains("/account/logout")&&!idleHtml.Contains("problem-row"),"Unenrolled participants see a waiting screen with logout instead of problems.");
    var scheduledAt=DateTimeOffset.UtcNow.AddHours(1);
    var scheduled=await contestStore.CreateAsync(new(){Title="Контест по расписанию",ProblemIds=[problem.Id],ParticipantIds=[generatedUser.Id],StartsAt=scheduledAt,ParticipantLimit=1,Languages=["Python"]});
    var waitingActor=new FixedActor(new(generatedUser.Id,generatedUser.DisplayName,false));
    var waitingContests=new ContestStore(factory,waitingActor);
    var waitingProblems=new ProblemStore(factory,waitingActor);
    var waitingSubmissions=new SubmissionStore(factory,waitingActor);
    var waitingScores=new StandingsStore(factory,waitingActor,waitingContests);
    var waitingContest=(await waitingContests.GetAllAsync()).Single();
    Check(waitingContest.Status==ContestStatus.Scheduled&&Math.Abs((waitingContest.StartsAt!.Value-scheduledAt).TotalMilliseconds)<1&&waitingContest.ProblemIds.Length==0&&waitingContest.ParticipantIds is null,"Assigned future contest exposes its start time without problems or roster.");
    Check((await waitingProblems.GetAllAsync()).Length==0&&(await waitingScores.GetViewAsync(scheduled.Id)).Contest is null,"Scheduled contests expose neither problems nor standings before start.");
    await PostForm(accessClient,"/account/login","/account/login",new(){{"Login",generated.Login},{"Password",generated.Password}});
    var waitingHtml=WebUtility.HtmlDecode(await accessClient.GetStringAsync("/problems/"+problem.Id));
    Check(waitingHtml.Contains("До начала осталось")&&!waitingHtml.Contains("code-editor")&&!waitingHtml.Contains("statement-content"),"Direct problem links show countdown without revealing the statement or editor.");
    Check((await accessClient.GetAsync($"/problems/{problem.Id}/pdf/ru")).StatusCode==HttpStatusCode.NotFound,"Scheduled participants cannot download PDF early.");
    try{await waitingSubmissions.SubmitAsync(Guid.NewGuid(),problem.Id,scheduled.Id,"Python","print(5)");throw new Exception("Early submission accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("judge.contestClosed"),"Scheduled contest rejects early submissions.");}
    try{await new ChatService(factory,waitingActor).SendAsync("early");throw new Exception("Early chat accepted.");}catch(UnauthorizedAccessException){Check(true,"Waiting participants cannot bypass the gate through chat.");}
    foreach(var ids in new[]{new[]{adminId},new[]{generatedUser.Id,progressUser.Id},new[]{"missing"},new[]{generatedUser.Id,generatedUser.Id}}){
        try{await contestStore.SetParticipantsAsync(scheduled.Id,ids);throw new Exception("Invalid roster accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("enrollment.invalid"),"Roster validates role, existence, capacity and duplicates.");}
    }
    try{await waitingContests.SetParticipantsAsync(scheduled.Id,[]);throw new Exception("Participant changed roster.");}catch(UnauthorizedAccessException){Check(true,"Only admins can change contest membership.");}
    try{await waitingContests.ScheduleAsync(scheduled.Id,scheduledAt.AddHours(1));throw new Exception("Participant scheduled contest.");}catch(UnauthorizedAccessException){Check(true,"Only admins can schedule contests.");}
    try{await contestStore.ScheduleAsync(scheduled.Id,DateTimeOffset.UtcNow.AddMinutes(-1));throw new Exception("Past schedule accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("schedule.invalid"),"Scheduling rejects past start times.");}
    await using(var db=await factory.CreateDbContextAsync()){
        var row=await db.Contests.SingleAsync(c=>c.Id==scheduled.Id);
        Check(JsonSerializer.Deserialize<JudgeProblem[]>(row.JudgeProblemsJson)!.Length==1,"Scheduling persists judging snapshots before automatic start.");
        row.StartsAt=DateTimeOffset.UtcNow.AddMinutes(-1);row.EndsAt=DateTimeOffset.UtcNow.AddHours(1);await db.SaveChangesAsync();
    }
    Check((await waitingProblems.GetAllAsync()).Single().Id==problem.Id,"Passing the start time opens exactly the assigned contest problems without a manual start.");
    await waitingSubmissions.SubmitAsync(Guid.NewGuid(),problem.Id,scheduled.Id,"Python","print(5)");
    Check((await waitingScores.GetViewAsync(scheduled.Id)).Rows.Length==1,"Automatically started contest accepts solutions and exposes live standings.");
    var currentHtml=WebUtility.HtmlDecode(await accessClient.GetStringAsync("/standings"));
    Check(currentHtml.Contains("Текущий контест")&&currentHtml.Contains("Контест по расписанию")&&currentHtml.Contains("<table"),"Participant standings automatically select the current contest.");
    Check((await waitingScores.GetViewAsync(contest.Id)).Contest is null,"Participants cannot access another contest's standings by ID.");
    await contestStore.SetParticipantsAsync(scheduled.Id,[]);
    Check((await waitingProblems.GetAllAsync()).Length==0&&(await waitingSubmissions.HistoryAsync(problem.Id)).Length==0&&(await waitingSubmissions.ProblemStatusesAsync()).Count==0,"Removing membership immediately revokes problems, submission history and progress.");
    Check((await accessClient.GetAsync($"/contests/{scheduled.Id}/standings.csv")).StatusCode==HttpStatusCode.NotFound&&(await accessClient.GetAsync($"/problems/{problem.Id}/pdf/ru")).StatusCode==HttpStatusCode.NotFound,"Existing cookies cannot bypass removal through CSV or PDF.");
    try{await waitingSubmissions.SubmitAsync(Guid.NewGuid(),problem.Id,scheduled.Id,"Python","print(5)");throw new Exception("Unenrolled submission accepted.");}catch(DomainException ex){Check(ex.Keys.Contains("judge.contestClosed"),"Removed participants cannot submit by contest ID.");}
    await contestStore.SetParticipantsAsync(scheduled.Id,[generatedUser.Id]);
    await using(var db=await factory.CreateDbContextAsync()){await db.Contests.Where(c=>c.Id==scheduled.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.StartsAt,scheduledAt).SetProperty(c=>c.EndsAt,scheduledAt.AddMinutes(120)));}
    var keysRu=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(project,"Resources","ru.json")))!;
    var keysEn=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(project,"Resources","en.json")))!;
    Check(keysRu.Keys.Order().SequenceEqual(keysEn.Keys.Order()),"RU and EN translation keys match.");
    {
        var liveConfig=Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(new Microsoft.Extensions.Configuration.ConfigurationBuilder(),new Dictionary<string,string?>{["ConnectionStrings:Database"]=connection}).Build();
        var live=new LiveEvents(liveConfig,Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveEvents>.Instance);
        var connected=new TaskCompletionSource();var mine=new TaskCompletionSource();var foreign=0;
        using var resync=live.Subscribe(e=>e.Kind=="resync",()=>{connected.TrySetResult();return Task.CompletedTask;});
        using var own=live.Subscribe(e=>e.Kind!="resync"&&e.AffectsUser("live-user"),()=>{mine.TrySetResult();return Task.CompletedTask;});
        using var other=live.Subscribe(e=>e.Kind!="resync"&&e.AffectsUser("someone-else"),()=>{Interlocked.Increment(ref foreign);return Task.CompletedTask;});
        await live.StartAsync(CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using(var db=await factory.CreateDbContextAsync()){
            await using var tx=await db.Database.BeginTransactionAsync();
            await LiveEvents.NotifyAsync(db,new("submission","live-user",null));
            await Task.Delay(500);
            Check(!mine.Task.IsCompleted,"Live events are delivered only after the transaction commits.");
            await tx.CommitAsync();
        }
        await mine.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        Check(foreign==0,"Live submission events reach only the affected user's pages.");
        await live.StopAsync(CancellationToken.None);
    }
    var testInfo=new {Url=admin.BaseAddress.ToString(),Login="admin_checks",Password=adminPassword,ParticipantLogin=recreated.Login,ParticipantPassword=recreated.Password,WaitingLogin=generated.Login,WaitingPassword=generated.Password,IdleLogin=idleCredentials.Login,IdlePassword=idleCredentials.Password,ScheduledContestId=scheduled.Id,ProgressContestId=progressContestId,Schema=schema,Folder=folder,ContestId=contest.Id,ProblemId=problem.Id,Pid=server.Id};
    await File.WriteAllTextAsync(Path.Combine(root,".local","ui-checks.json"),JsonSerializer.Serialize(testInfo));
    Console.WriteLine($"All {count} checks passed.");
    if(keep){
        Console.WriteLine("Isolated browser test server is ready on port 5081.");
        var cancellation=new CancellationTokenSource();
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        try{while(!File.Exists(Path.Combine(folder,"stop"))) await Task.Delay(500,cancellation.Token);}catch(OperationCanceledException){}
    }
}
catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
finally{
    if(!server.HasExited)server.Kill(entireProcessTree:true);
    await server.WaitForExitAsync();
    await File.WriteAllTextAsync(Path.Combine(folder,"server.log"),await stdout+"\n"+await stderr);
    await using var sql=new NpgsqlConnection(connection);await sql.OpenAsync();
    if(!Regex.IsMatch(schema,"^checks_[a-f0-9]{32}$"))throw new InvalidOperationException("Unsafe test schema.");
    await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE",sql).ExecuteNonQueryAsync();
}

sealed class FixedActor(Actor actor):ICurrentActor{
    public Task<Actor> GetAsync()=>Task.FromResult(actor);
    public Task<Actor> RequireAdminAsync()=>actor.IsAdmin?Task.FromResult(actor):throw new UnauthorizedAccessException();
}
sealed class TestAuthState(ClaimsPrincipal principal):AuthenticationStateProvider{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()=>Task.FromResult(new AuthenticationState(principal));
}








