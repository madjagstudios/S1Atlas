using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using S1Atlas.Core.Indexing;
using NormalizedSymbolKind = S1Atlas.Core.Indexing.SymbolKind;

namespace S1Atlas.Indexing.Source;

public sealed class RoslynSourceIndexer
{
    public IReadOnlyList<NormalizedSymbol> Index(string source, CodebaseKind codebase, CodeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (codebase == CodebaseKind.ScheduleI && channel != CodeChannel.Installed)
            throw new ArgumentException("Schedule I source can only be indexed in the Installed channel.", nameof(channel));
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var symbols = new List<NormalizedSymbol>();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var qualifiedType = QualifiedName(type);
            symbols.Add(new NormalizedSymbol(codebase, channel, NormalizedSymbolKind.Type, qualifiedType, CanonicalSignatureRenderer.RenderType(qualifiedType), true, null, type.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            foreach (var member in type.Members)
                AddMember(symbols, type, qualifiedType, member, codebase, channel);
        }
        return symbols.OrderBy(symbol => symbol.Signature, StringComparer.Ordinal).ThenBy(symbol => symbol.Kind).ToArray();
    }

    private static void AddMember(List<NormalizedSymbol> symbols, TypeDeclarationSyntax type, string qualifiedType, MemberDeclarationSyntax member, CodebaseKind codebase, CodeChannel channel)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                {
                    var parameters = method.ParameterList.Parameters.Select(parameter => ParameterType(parameter)).ToArray();
                    var signature = CanonicalSignatureRenderer.RenderMethod(qualifiedType, method.Identifier.Text, method.ReturnType.ToString(), parameters, method.TypeParameterList?.Parameters.Count ?? 0);
                    symbols.Add(new NormalizedSymbol(codebase, channel, NormalizedSymbolKind.Method, qualifiedType + "::" + method.Identifier.Text, signature, true, null, Line(member)));
                    break;
                }
            case ConstructorDeclarationSyntax constructor:
                {
                    var parameters = constructor.ParameterList.Parameters.Select(ParameterType).ToArray();
                    var signature = CanonicalSignatureRenderer.RenderMethod(qualifiedType, ".ctor", "void", parameters);
                    symbols.Add(new NormalizedSymbol(codebase, channel, NormalizedSymbolKind.Constructor, qualifiedType + "::.ctor", signature, true, null, Line(member)));
                    break;
                }
            case PropertyDeclarationSyntax property:
                symbols.Add(new NormalizedSymbol(codebase, channel, NormalizedSymbolKind.Property, qualifiedType + "::" + property.Identifier.Text, CanonicalSignatureRenderer.RenderType(property.Type.ToString()) + " " + property.Identifier.Text, true, null, Line(member)));
                break;
            case EventDeclarationSyntax @event:
                symbols.Add(new NormalizedSymbol(codebase, channel, NormalizedSymbolKind.Event, qualifiedType + "::" + @event.Identifier.Text, CanonicalSignatureRenderer.RenderType(@event.Type.ToString()) + " " + @event.Identifier.Text, true, null, Line(member)));
                break;
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                    symbols.Add(new NormalizedSymbol(codebase, channel, NormalizedSymbolKind.Field, qualifiedType + "::" + variable.Identifier.Text, CanonicalSignatureRenderer.RenderType(field.Declaration.Type.ToString()) + " " + variable.Identifier.Text, true, null, Line(member)));
                break;
        }
    }

    private static string QualifiedName(TypeDeclarationSyntax type)
    {
        var parts = new List<string> { type.Identifier.Text };
        foreach (var parent in type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()) parts.Insert(0, parent.Identifier.Text);
        foreach (var @namespace in type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()) parts.Insert(0, @namespace.Name.ToString());
        return string.Join('.', parts);
    }

    private static string ParameterType(ParameterSyntax parameter)
    {
        var prefix = parameter.Modifiers.Any(SyntaxKind.RefKeyword) ? "ref " : parameter.Modifiers.Any(SyntaxKind.InKeyword) ? "in " : parameter.Modifiers.Any(SyntaxKind.OutKeyword) ? "out " : string.Empty;
        return prefix + (parameter.Type?.ToString() ?? "object");
    }

    private static int Line(MemberDeclarationSyntax member) => member.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
