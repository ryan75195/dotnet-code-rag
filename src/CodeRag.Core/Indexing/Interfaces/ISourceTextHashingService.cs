namespace CodeRag.Core.Indexing.Interfaces;

public interface ISourceTextHashingService
{
    string Hash(string sourceText);
}
