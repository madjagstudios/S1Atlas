namespace S1Atlas.Core.Indexing;

public sealed record NormalizedTypeReference(string CanonicalName)
{
    public static NormalizedTypeReference Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new NormalizedTypeReference(CanonicalSignatureRenderer.RenderType(text));
    }

    public override string ToString() => CanonicalName;
}
