namespace CodeRag.Core.Indexing;

public sealed record ChunkModifiers(
    bool IsStatic,
    bool IsAbstract,
    bool IsSealed,
    bool IsVirtual,
    bool IsOverride,
    bool IsAsync,
    bool IsPartial,
    bool IsReadonly,
    bool IsExtern,
    bool IsUnsafe,
    bool IsExtensionMethod,
    bool IsGeneric);
