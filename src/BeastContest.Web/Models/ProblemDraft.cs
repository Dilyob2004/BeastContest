namespace BeastContest.Web.Models;

public sealed class ProblemDraft
{
    public string TitleRu { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string StatementRu { get; set; } = "";
    public string StatementEn { get; set; } = "";
    public string InputRu { get; set; } = "";
    public string InputEn { get; set; } = "";
    public string OutputRu { get; set; } = "";
    public string OutputEn { get; set; } = "";
    public string SampleInput { get; set; } = "";
    public string SampleOutput { get; set; } = "";
    public List<BeastContest.Web.Services.TestCaseDraft> Tests { get; set; } = [];
    public string Difficulty { get; set; } = "easy";
    public int TimeMs { get; set; } = 1000;
    public int MemoryMb { get; set; } = 256;
    public List<string> Tags { get; set; } = [];
    public byte[]? PdfRu { get; set; }
    public byte[]? PdfEn { get; set; }
    public string CheckerMode { get; set; } = BeastContest.Web.Services.CheckerModes.Tokens;
    public double CheckerEpsilon { get; set; } = BeastContest.Web.Services.CheckerModes.DefaultEpsilon;
    public string CheckerSource { get; set; } = "";
    public const int MaxPdfBytes = 5 * 1024 * 1024;

    public string[] Validate()
    {
        var errors = new List<string>();
        if ((string.IsNullOrWhiteSpace(TitleRu) && string.IsNullOrWhiteSpace(TitleEn)) || TitleRu.Length > 100 || TitleEn.Length > 100) errors.Add("validation.problemTitle");
        if (string.IsNullOrWhiteSpace(StatementRu) && string.IsNullOrWhiteSpace(StatementEn) && PdfRu is null && PdfEn is null) errors.Add("validation.statement");
        if (new[]{StatementRu, StatementEn, InputRu, InputEn, OutputRu, OutputEn, SampleInput, SampleOutput}.Any(s => s.Length > 20000)) errors.Add("validation.problemText");
        if (TimeMs is < 100 or > 30000) errors.Add("validation.time");
        if (MemoryMb is < 16 or > 2048) errors.Add("validation.memory");
        if (!new[]{"easy","medium","hard"}.Contains(Difficulty) || Tags.Any(t => !DemoCatalog.Tags.Contains(t))) errors.Add("validation.problemOptions");
        if (!ValidPdf(PdfRu) || !ValidPdf(PdfEn)) errors.Add("validation.pdf");
        errors.AddRange(BeastContest.Web.Services.CheckerModes.Validate(CheckerMode, CheckerEpsilon, CheckerSource));
        return [.. errors];
    }

    public static bool ValidPdf(byte[]? data) => data is null || (data.Length is >= 5 and <= MaxPdfBytes && data.AsSpan(0,5).SequenceEqual("%PDF-"u8));
}
