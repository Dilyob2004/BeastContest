using BeastContest.Web.Data;
using BeastContest.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace BeastContest.Web.Services;

public static class ParticipantAccess
{
    /// <summary>
    /// The contest a participant works in: running, then the next scheduled one (waiting screen),
    /// then the latest finished one. A draft has no date and is not announced yet, so it never hides
    /// a finished contest's problems, results and chat; it only shows the waiting screen when nothing else exists.
    /// </summary>
    public static Contest? Current(IEnumerable<Contest> contests) =>
        contests.Where(c=>c.Status==ContestStatus.Running).OrderBy(c=>c.StartsAt).FirstOrDefault()
        ??contests.Where(c=>c.Status==ContestStatus.Scheduled).OrderBy(c=>c.StartsAt).FirstOrDefault()
        ??contests.Where(c=>c.Status==ContestStatus.Finished).OrderByDescending(c=>c.EndsAt).FirstOrDefault()
        ??contests.Where(c=>c.Status==ContestStatus.Draft).OrderBy(c=>c.CreatedAt).FirstOrDefault();

    public static async Task<Contest?> CurrentAsync(AppDbContext db,Actor actor){
        if(!actor.IsAuthenticated)return null;
        var contests=await db.Contests.AsNoTracking().Where(c=>!c.IsDemo&&c.ParticipantIds.Contains(actor.Id)).ToArrayAsync();
        return Current(contests.Select(ContestStore.ToModel));
    }
    public static bool Open(Contest? contest)=>contest?.Status is ContestStatus.Running or ContestStatus.Finished;
}
