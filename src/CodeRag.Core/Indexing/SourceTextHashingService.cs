using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

public sealed class SourceTextHashingService : ISourceTextHashingService
{
    public string Hash(string sourceText)
    {
        var bytes = Encoding.UTF8.GetBytes(sourceText);
        var hash = SHA256.HashData(bytes);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
