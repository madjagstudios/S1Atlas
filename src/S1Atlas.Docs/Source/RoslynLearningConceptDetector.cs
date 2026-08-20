using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace S1Atlas.Docs.Source;

public sealed record LearningConcept(string Label, string EvidenceText, string SourceAnchor);

public sealed class RoslynLearningConceptDetector
{
    public IReadOnlyList<LearningConcept> Detect(string displayedSource)
    {
        ArgumentNullException.ThrowIfNull(displayedSource);
        var root = CSharpSyntaxTree.ParseText(displayedSource).GetRoot();
        var concepts = new List<LearningConcept>();
        AddIf(root.DescendantNodesAndSelf().Any(node => node is TypeParameterListSyntax or GenericNameSyntax), "contains generic syntax", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<ConditionalAccessExpressionSyntax>().Any(), "contains a null-conditional operator", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Any(node => node.IsKind(SyntaxKind.CoalesceExpression)), "contains a null-coalescing operator", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<QueryExpressionSyntax>().Any(), "contains a LINQ query expression", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().Any(), "contains object creation syntax", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(), "contains invocation syntax", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<LambdaExpressionSyntax>().Any(), "contains lambda syntax", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<PropertyDeclarationSyntax>().Any(), "contains property declaration syntax", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<EventDeclarationSyntax>().Any(), "contains event declaration syntax", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<ConstructorDeclarationSyntax>().Any(), "contains constructor declaration syntax", concepts);
        AddIf(root.DescendantNodesAndSelf().OfType<MemberDeclarationSyntax>().Any(member => member.Modifiers.Any(SyntaxKind.StaticKeyword)), "contains static member syntax", concepts);
        return concepts;
    }

    private static void AddIf(bool condition, string label, ICollection<LearningConcept> concepts)
    {
        if (condition) concepts.Add(new LearningConcept(label, "detected in the displayed source span", "source-span"));
    }
}
