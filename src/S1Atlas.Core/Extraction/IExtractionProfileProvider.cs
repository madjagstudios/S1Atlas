namespace S1Atlas.Core.Extraction;

public interface IExtractionProfileProvider
{
    ResolvedExtractionProfile GetRequired(string profileId);
}
