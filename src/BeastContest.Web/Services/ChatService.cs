using BeastContest.Web.Data;
using Microsoft.EntityFrameworkCore;
namespace BeastContest.Web.Services;
public sealed class ChatService(IDbContextFactory<AppDbContext> factory, ICurrentActor actor)
{
    private async Task<(Actor Actor,string Participant)> AccessAsync(string? participant) {
        var current=await actor.GetAsync();
        if(!current.IsAuthenticated) throw new UnauthorizedAccessException();
        if(!current.IsAdmin) {
            if(participant is not null && participant!=current.Id) throw new UnauthorizedAccessException();
            await using var accessDb=await factory.CreateDbContextAsync();
            if(!ParticipantAccess.Open(await ParticipantAccess.CurrentAsync(accessDb,current)))throw new UnauthorizedAccessException();
            return (current,current.Id);
        }
        await using var db=await factory.CreateDbContextAsync();
        if(string.IsNullOrWhiteSpace(participant) || !await db.Users.AnyAsync(u=>u.Id==participant)) throw new UnauthorizedAccessException();
        return (current,participant);
    }
    public async Task<ChatMessage[]> ReadAsync(string? participant=null) {
        var access=await AccessAsync(participant);
        await using var db=await factory.CreateDbContextAsync();
        var messages=await db.ChatMessages.AsNoTracking().Where(m=>m.ParticipantId==access.Participant).OrderByDescending(m=>m.Id).Take(100).ToArrayAsync();
        return messages.Reverse().ToArray();
    }
    public async Task SendAsync(string body,string? participant=null) {
        var access=await AccessAsync(participant);
        if(string.IsNullOrWhiteSpace(body) || body.Trim().Length>2000) throw new ArgumentException("Message must contain 1–2000 characters.");
        await using var db=await factory.CreateDbContextAsync();
        db.ChatMessages.Add(new ChatMessage {ParticipantId=access.Participant,SenderId=access.Actor.Id,SenderName=access.Actor.Name,FromAdmin=access.Actor.IsAdmin,Body=body.Trim(),SentAt=DateTimeOffset.UtcNow});
        await db.SaveChangesAsync();
        await LiveEvents.NotifyAsync(db,new("chat",ParticipantId:access.Participant));
    }
}
