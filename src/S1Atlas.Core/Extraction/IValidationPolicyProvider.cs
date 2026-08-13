namespace S1Atlas.Core.Extraction;

public interface IValidationPolicyProvider
{
    ResolvedValidationPolicy GetRequired(string policyId);
}
