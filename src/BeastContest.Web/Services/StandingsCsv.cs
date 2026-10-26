using System.Globalization;
using System.Text;
using BeastContest.Web.Models;

namespace BeastContest.Web.Services;

public static class StandingsCsv
{
    public static byte[] Create(StandingsView view,string language,Translations translations){
        var contest=view.Contest??throw new ArgumentException("A contest is required.",nameof(view));
        var csv=new StringBuilder();
        string T(string key)=>translations.Get(language,key);
        var icpc=contest.Scoring==ScoringMode.Icpc;
        var headers=new List<string>{T("csv.contest"),T("csv.scoring"),T("csv.view"),T("csv.snapshot"),T("csv.rank"),T("standings.participant"),T(icpc?"standings.solved":"standings.total")};
        if(icpc)headers.Add(T("standings.penalty"));
        headers.AddRange([T("standings.time"),T("standings.attempts")]);
        foreach(var letter in Enumerable.Range(0,contest.ProblemIds.Length).Select(DemoCatalog.Number))headers.AddRange([letter,letter+" · "+T("standings.time"),letter+" · "+T("standings.attempts")]);
        Write(headers);
        var title=language=="en"?contest.Title.En:contest.Title.Ru;
        if(string.IsNullOrWhiteSpace(title))title=language=="en"?contest.Title.Ru:contest.Title.En;
        var frozen=view.FrozenAt is not null&&!view.IsAdmin;
        foreach(var row in view.Rows){
            var cells=new List<string>{title,T(icpc?"scoring.icpc":"scoring.points"),T(frozen?"csv.frozen":"csv.current"),frozen?view.FrozenAt!.Value.ToString("O",CultureInfo.InvariantCulture):"",row.Rank.ToString(CultureInfo.InvariantCulture),row.Name,row.Score.ToString(CultureInfo.InvariantCulture)};
            if(icpc)cells.Add(row.Penalty.ToString(CultureInfo.InvariantCulture));
            cells.Add(StandingsStore.FormatTime(row.TimeSeconds));cells.Add(row.Attempts?.ToString(CultureInfo.InvariantCulture)??"");
            for(var i=0;i<contest.ProblemIds.Length;i++){
                cells.Add(row.Results.ElementAtOrDefault(i)??"—");
                cells.Add(StandingsStore.FormatTime(row.ProblemSeconds?.ElementAtOrDefault(i)));
                cells.Add(row.ProblemAttempts?.ElementAtOrDefault(i).ToString(CultureInfo.InvariantCulture)??"");
            }
            Write(cells);
        }
        // UTF-8 BOM preserves Cyrillic in spreadsheet applications.
        return [..Encoding.UTF8.GetPreamble(),..Encoding.UTF8.GetBytes(csv.ToString())];
        void Write(IEnumerable<string> fields)=>csv.AppendJoin(';',fields.Select(Cell)).Append("\r\n");
    }

    private static string Cell(string value){
        // Quoting alone does not prevent spreadsheet formula execution.
        var trimmed=value.TrimStart();
        if((trimmed.Length>0&&"=+-@".Contains(trimmed[0]))||(value.Length>0&&char.IsControl(value[0])))value="'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }
}

// Used only after a fresh HTTP identity/security-stamp check.
internal sealed class ExportActor(Actor actor):ICurrentActor
{
    public Task<Actor> GetAsync()=>Task.FromResult(actor);
    public Task<Actor> RequireAdminAsync()=>actor.IsAdmin?Task.FromResult(actor):throw new UnauthorizedAccessException();
}
