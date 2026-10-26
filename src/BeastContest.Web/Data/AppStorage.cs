namespace BeastContest.Web.Data;

public sealed class AppStorage(IConfiguration configuration, IWebHostEnvironment environment)
{
    public string Root { get; } = Path.GetFullPath(configuration["Storage:Path"]
        ?? Path.Combine(environment.ContentRootPath, "App_Data"));
}
