using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace S1Atlas.TestSupport;

/// <summary>Compiles C# source into a scratch assembly named after the output file.</summary>
public static class ScratchAssembly
{
    public static void Compile(string source, string outputPath, params string[] extraReferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(extraReferences).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(outputPath),
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var output = File.Create(outputPath);
        var result = compilation.Emit(output);
        if (!result.Success)
            throw new InvalidOperationException("Scratch fixture compilation failed: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
}
