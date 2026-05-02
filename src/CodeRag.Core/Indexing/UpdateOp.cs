namespace CodeRag.Core.Indexing;

public sealed record UpdateOp(long ChunkId, CodeChunk Chunk, bool ContentChanged);
