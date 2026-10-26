using System.Text;
using BeastContest.Web.Models;
namespace BeastContest.Web.Services;
public sealed record SolutionFile(string Language,string Code)
{
    public const int MaxBytes=65536;
    public static SolutionFile Read(string fileName,byte[] bytes){
        if(bytes.Length>MaxBytes)throw new DomainException("judge.fileError");
        var language=Path.GetExtension(fileName).ToLowerInvariant() switch{
            ".cpp" or ".cc" or ".cxx"=>"C++",".py"=>"Python",".cs"=>"C#",".java"=>"Java",".go"=>"Go",".hs"=>"Haskell",".js"=>"JavaScript",_=>throw new DomainException("judge.fileError")};
        string code;try{code=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');}catch(DecoderFallbackException){throw new DomainException("judge.fileError");}
        if(code.Contains('\0')||string.IsNullOrWhiteSpace(code))throw new DomainException("judge.fileError");
        return new(language,code);
    }
}
