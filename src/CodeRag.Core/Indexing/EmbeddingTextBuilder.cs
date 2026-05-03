using System.Text;

namespace CodeRag.Core.Indexing;

internal static class EmbeddingTextBuilder
{
    public static string BuildEmbeddingInput(CodeChunk chunk)
    {
        var sb = new StringBuilder();
        sb.Append("FQN: ").AppendLine(chunk.FullyQualifiedSymbolName);
        sb.Append("Kind: ").AppendLine(chunk.SymbolKind);
        if (!string.IsNullOrEmpty(chunk.ParentSymbolFullyQualifiedName))
        {
            sb.Append("Parent: ").AppendLine(chunk.ParentSymbolFullyQualifiedName);
        }
        if (!string.IsNullOrEmpty(chunk.SymbolSignatureDisplay))
        {
            sb.Append("Signature: ").AppendLine(chunk.SymbolSignatureDisplay);
        }
        if (!string.IsNullOrEmpty(chunk.XmlDocSummary))
        {
            sb.Append("Summary: ").AppendLine(chunk.XmlDocSummary);
        }
        sb.AppendLine();
        sb.Append(chunk.SourceText);
        return sb.ToString();
    }
}
