using System.Threading.RateLimiting;
using BeastContest.Web.Components;
using BeastContest.Web.Data;
using BeastContest.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder((args.Contains("--create-user") || args.Contains("--reset-admin")) ? [] : args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
var storage = new AppStorage(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(storage);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storage.Root,"keys")))
    .SetApplicationName("BeastContest");
var connection=builder.Configuration.GetConnectionString("Database");
if(string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Database connection is missing. Run Start-BeastContest.bat.");
builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddIdentityCore<ApplicationUser>(options => {
    options.Password.RequiredLength=6; // Role-specific limits are checked by AccountService/AccountCommand.
    options.Password.RequireDigit=false; options.Password.RequireLowercase=false;
    options.Password.RequireUppercase=false; options.Password.RequireNonAlphanumeric=false;
    options.Password.RequiredUniqueChars=3;
    options.User.AllowedUserNameCharacters="abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";
    options.Lockout.MaxFailedAccessAttempts=5;
    options.Lockout.DefaultLockoutTimeSpan=TimeSpan.FromMinutes(5);
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>().AddSignInManager().AddDefaultTokenProviders();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options => {
    options.Cookie.Name="BeastContest.Auth";
    options.Cookie.HttpOnly=true; options.Cookie.SameSite=SameSiteMode.Lax;
    options.LoginPath="/account/login"; options.AccessDeniedPath="/access-denied";
    options.ExpireTimeSpan=TimeSpan.FromHours(8);
    options.SlidingExpiration=true;
});
builder.Services.Configure<SecurityStampValidatorOptions>(options=>options.ValidationInterval=TimeSpan.FromSeconds(30));
builder.Services.AddAuthorization(options => options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider,IdentityRevalidator>();
builder.Services.AddScoped<ICurrentActor,CurrentActor>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents().AddHubOptions(options=>options.MaximumReceiveMessageSize=1024*1024);
builder.Services.AddRazorPages();
builder.Services.AddSingleton<Translations>();
builder.Services.AddScoped<UiState>();
builder.Services.AddScoped<ContestStore>();
builder.Services.AddScoped<ProblemStore>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<TestSuiteStore>();
builder.Services.AddScoped<SubmissionStore>();
builder.Services.AddScoped<JudgeMonitorStore>();
builder.Services.AddScoped<RejudgeStore>();
builder.Services.AddScoped<StandingsStore>();
builder.Services.AddSingleton<DatabaseBootstrapper>();
builder.Services.AddSingleton<LiveEvents>();
builder.Services.AddHostedService(services=>services.GetRequiredService<LiveEvents>());
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode=StatusCodes.Status429TooManyRequests;
    options.AddPolicy("account", context=>RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _=>new FixedWindowRateLimiterOptions {PermitLimit=240,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
var app=builder.Build();
_ = app.Services.GetRequiredService<Translations>();
await app.Services.GetRequiredService<DatabaseBootstrapper>().InitializeAsync();
if((args.Contains("--create-user") || args.Contains("--reset-admin"))) { Environment.ExitCode=await AccountCommand.RunAsync(app.Services,args); return; }
if(!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error",createScopeForErrors:true);

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages();
app.MapGet("/health",async (IDbContextFactory<AppDbContext> factory)=>{
    await using var db=await factory.CreateDbContextAsync();
    return await db.Database.CanConnectAsync()
        ? Results.Ok(new{status="ok",stage="submission-queue",database="postgresql"})
        : Results.StatusCode(503);
}).AllowAnonymous();
app.MapGet("/problems/{id}/pdf/{language}",async (string id,string language,HttpContext http,UserManager<ApplicationUser> users,IDbContextFactory<AppDbContext> factory)=>{
    if(language is not ("ru" or "en")) return Results.NotFound();
    var user=await users.GetUserAsync(http.User);
    if(user is null || user.Disabled || user.SecurityStamp!=http.User.FindFirst("AspNet.Identity.SecurityStamp")?.Value) return Results.Unauthorized();
    await using var db=await factory.CreateDbContextAsync();
    var current=new Actor(user.Id,user.DisplayName,await users.IsInRoleAsync(user,"Admin"));
    var visible=await ProblemStore.VisibleIdsAsync(db,current);
    var query=db.Problems.Where(p=>p.Id==id && (current.IsAdmin || visible.Contains(p.Id)));
    var bytes=language=="ru" ? await query.Select(p=>p.PdfRu).SingleOrDefaultAsync() : await query.Select(p=>p.PdfEn).SingleOrDefaultAsync();
    if(bytes is null) return Results.NotFound();
    http.Response.Headers.XContentTypeOptions="nosniff";
    return Results.File(bytes,"application/pdf",$"problem-{id}-{language}.pdf");
}).RequireAuthorization();
app.MapGet("/contests/{id:guid}/standings.csv",async (Guid id,string? lang,HttpContext http,UserManager<ApplicationUser> users,IDbContextFactory<AppDbContext> factory,Translations translations)=>{
    if(lang is not (null or "ru" or "en"))return Results.BadRequest();
    var user=await users.GetUserAsync(http.User);
    if(user is null || user.Disabled || user.SecurityStamp!=http.User.FindFirst("AspNet.Identity.SecurityStamp")?.Value)return Results.Unauthorized();
    var actor=new ExportActor(new(user.Id,user.DisplayName,await users.IsInRoleAsync(user,"Admin")));
    var scores=new StandingsStore(factory,actor,new ContestStore(factory,actor));
    var view=await scores.GetViewAsync(id);
    if(view.Contest is null)return Results.NotFound();
    var language=lang??"ru";
    http.Response.Headers.CacheControl="private, no-store";
    http.Response.Headers.XContentTypeOptions="nosniff";
    var visibility=view.FrozenAt is not null&&!view.IsAdmin?"frozen":"current";
    return Results.File(StandingsCsv.Create(view,language,translations),"text/csv; charset=utf-8",$"standings-{id}-{visibility}-{language}.csv");
}).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
public partial class Program { }








