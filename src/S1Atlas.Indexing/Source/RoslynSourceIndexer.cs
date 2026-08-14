using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using S1Atlas.Core.Indexing;
using NormalizedSymbolKind = S1Atlas.Core.Indexing.SymbolKind;

namespace S1Atlas.Indexing.Source;

public sealed class RoslynSourceIndexer
{
    public IReadOnlyList<NormalizedSymbol> Index(string source, CodebaseKind codebase, CodeChannel channel, string sourceFile = "source.cs")
    {
        ArgumentNullException.ThrowIfNull(source);
        if (codebase == CodebaseKind.ScheduleI && channel != CodeChannel.Installed)
            throw new ArgumentException("Schedule I source can only be indexed in the Installed channel.", nameof(channel));
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var symbols = new List<NormalizedSymbol>();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (string.IsNullOrWhiteSpace(type.Identifier.Text)) continue;
            var qualifiedType = QualifiedName(type);
            if (string.IsNullOrWhiteSpace(qualifiedType) || qualifiedType.Contains("..", StringComparison.Ordinal)) continue;
            try
            {
                symbols.Add(CreateSymbol(codebase, channel, NormalizedSymbolKind.Type, qualifiedType, CanonicalSignatureRenderer.RenderType(qualifiedType), sourceFile, type));
            }
            catch (ArgumentException exception) when (exception.ParamName == "text")
            {
                continue;
            }
            foreach (var member in type.Members)
            {
                try
                {
                    AddMember(symbols, type, qualifiedType, member, codebase, channel, sourceFile);
                }
                catch (ArgumentException exception) when (exception.ParamName == "text")
                {
                }
            }
        }
        return symbols.OrderBy(symbol => symbol.Signature, StringComparer.Ordinal).ThenBy(symbol => symbol.Kind).ToArray();
    }

    private static void AddMember(List<NormalizedSymbol> symbols, TypeDeclarationSyntax type, string qualifiedType, MemberDeclarationSyntax member, CodebaseKind codebase, CodeChannel channel, string sourceFile)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                {
                    if (string.IsNullOrWhiteSpace(method.Identifier.Text)) break;
                    var parameters = method.ParameterList.Parameters.Select(parameter => ParameterType(parameter)).ToArray();
                    var signature = CanonicalSignatureRenderer.RenderMethod(qualifiedType, method.Identifier.Text, method.ReturnType.ToString(), parameters, method.TypeParameterList?.Parameters.Count ?? 0);
                    symbols.Add(CreateSymbol(codebase, channel, NormalizedSymbolKind.Method, signature, signature, sourceFile, member));
                    break;
                }
            case ConstructorDeclarationSyntax constructor:
                {
                    if (string.IsNullOrWhiteSpace(constructor.Identifier.Text)) break;
                    var parameters = constructor.ParameterList.Parameters.Select(ParameterType).ToArray();
                    var signature = CanonicalSignatureRenderer.RenderMethod(qualifiedType, ".ctor", "void", parameters);
                    symbols.Add(CreateSymbol(codebase, channel, NormalizedSymbolKind.Constructor, signature, signature, sourceFile, member));
                    break;
                }
            case PropertyDeclarationSyntax property:
                {
                    if (string.IsNullOrWhiteSpace(property.Identifier.Text)) break;
                    var signature = CanonicalSignatureRenderer.RenderType(property.Type.ToString()) + " " + property.Identifier.Text;
                    symbols.Add(CreateSymbol(codebase, channel, NormalizedSymbolKind.Property, qualifiedType + "::" + signature, signature, sourceFile, member));
                    break;
                }
            case EventDeclarationSyntax @event:
                {
                    if (string.IsNullOrWhiteSpace(@event.Identifier.Text)) break;
                    var signature = CanonicalSignatureRenderer.RenderType(@event.Type.ToString()) + " " + @event.Identifier.Text;
                    symbols.Add(CreateSymbol(codebase, channel, NormalizedSymbolKind.Event, qualifiedType + "::" + signature, signature, sourceFile, member));
                    break;
                }
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    if (string.IsNullOrWhiteSpace(variable.Identifier.Text)) continue;
                    var signature = CanonicalSignatureRenderer.RenderType(field.Declaration.Type.ToString()) + " " + variable.Identifier.Text;
                    symbols.Add(CreateSymbol(codebase, channel, NormalizedSymbolKind.Field, qualifiedType + "::" + signature, signature, sourceFile, member));
                }
                break;
        }
    }

    private static NormalizedSymbol CreateSymbol(
        CodebaseKind codebase,
        CodeChannel channel,
        NormalizedSymbolKind kind,
        string qualifiedName,
        string signature,
        string sourceFile,
        SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return new NormalizedSymbol(
            codebase,
            channel,
            kind,
            qualifiedName,
            signature,
            true,
            sourceFile,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    private static string QualifiedName(TypeDeclarationSyntax type)
    {
        var parts = new List<string> { type.Identifier.Text };
        foreach (var parent in type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Where(parent => !string.IsNullOrWhiteSpace(parent.Identifier.Text))) parts.Insert(0, parent.Identifier.Text);
        foreach (var @namespace in type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()) parts.Insert(0, @namespace.Name.ToString());
        return string.Join('.', parts);
    }

    private static string ParameterType(ParameterSyntax parameter)
    {
        var prefix = parameter.Modifiers.Any(SyntaxKind.RefKeyword) ? "ref " : parameter.Modifiers.Any(SyntaxKind.InKeyword) ? "in " : parameter.Modifiers.Any(SyntaxKind.OutKeyword) ? "out " : string.Empty;
        return prefix + (parameter.Type?.ToString() ?? "object");
    }
}
