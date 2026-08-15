using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using S1Atlas.Extraction.Scene;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

public sealed class ParserIsolationTests
{
    private const string ParserNamespace = "AssetsTools.NET";

    [Fact]
    public void ProductionAssemblySignatures_DoNotLeakParserTypesOutsideAdapter()
    {
        var extraction = typeof(AssetsToolsUnitySerializedFileParser).Assembly;
        var repositoryRoot = FindRepositoryRoot();
        var prohibitedAssemblyPaths = new[]
        {
            typeof(S1Atlas.Core.Hashing.IFileHasher).Assembly.Location,
            typeof(SqliteAtlasRepository).Assembly.Location,
            GetProjectAssemblyPath(repositoryRoot, "S1Atlas.Indexing"),
            GetProjectAssemblyPath(repositoryRoot, "S1Atlas.Cli")
        };

        foreach (var assemblyPath in prohibitedAssemblyPaths)
        {
            AssertNoParserMetadataReferences(assemblyPath);
        }

        var leaks = FindSignatureLeaks(extraction, IsAdapterImplementationType)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(leaks);
    }

    [Fact]
    public void SourceAndProjectReferences_ConfineParserDependencyToExtractionAdapter()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var adapterPath = Path.GetFullPath(Path.Combine(
            sourceRoot,
            "S1Atlas.Extraction",
            "Scene",
            "AssetsToolsUnitySerializedFileParser.cs"));
        var parserSourceFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(ParserNamespace, StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal([adapterPath], parserSourceFiles, StringComparer.OrdinalIgnoreCase);

        var projectsWithParserPackage = Directory
            .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(ReferencesParserPackage)
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(
            [Path.GetFullPath(Path.Combine(sourceRoot, "S1Atlas.Extraction", "S1Atlas.Extraction.csproj"))],
            projectsWithParserPackage,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindSignatureLeaks(
        Assembly assembly,
        Func<Type, bool> allowedType)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (allowedType(type))
            {
                continue;
            }

            foreach (var referencedType in GetSignatureTypes(type))
            {
                foreach (var expandedType in ExpandType(referencedType))
                {
                    if (expandedType.Namespace?.StartsWith(ParserNamespace, StringComparison.Ordinal) == true)
                    {
                        yield return $"{assembly.GetName().Name}:{type.FullName}->{expandedType.FullName}";
                    }
                }
            }
        }
    }

    private static IEnumerable<Type> GetSignatureTypes(Type type)
    {
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var item in type.GetInterfaces())
        {
            yield return item;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var constraint in argument.GetGenericParameterConstraints())
            {
                yield return constraint;
            }
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(flags))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(flags))
        {
            yield return property.PropertyType;
            foreach (var parameter in property.GetIndexParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var @event in type.GetEvents(flags))
        {
            if (@event.EventHandlerType is not null)
            {
                yield return @event.EventHandlerType;
            }
        }

        foreach (var method in type.GetMethods(flags).Cast<MethodBase>()
                     .Concat(type.GetConstructors(flags)))
        {
            if (method is MethodInfo methodInfo)
            {
                yield return methodInfo.ReturnType;
            }

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            foreach (var argument in method is MethodInfo genericMethod
                         ? genericMethod.GetGenericArguments()
                         : [])
            {
                foreach (var constraint in argument.GetGenericParameterConstraints())
                {
                    yield return constraint;
                }
            }
        }
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nested in ExpandType(elementType))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in ExpandType(argument))
            {
                yield return nested;
            }
        }
    }

    private static bool IsAdapterImplementationType(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current == typeof(AssetsToolsUnitySerializedFileParser))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReferencesParserPackage(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document.Descendants("PackageReference").Any(element =>
            string.Equals(
                (string?)element.Attribute("Include"),
                ParserNamespace,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNoParserMetadataReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var referencedAssemblies = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name));
        Assert.DoesNotContain(
            referencedAssemblies,
            name => name.StartsWith(ParserNamespace, StringComparison.Ordinal));

        var referencedTypeNamespaces = metadata.TypeReferences.Select(handle =>
            metadata.GetString(metadata.GetTypeReference(handle).Namespace));
        Assert.DoesNotContain(
            referencedTypeNamespaces,
            @namespace => @namespace.StartsWith(ParserNamespace, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate S1Atlas.sln above '{AppContext.BaseDirectory}'.");
    }

    private static string GetProjectAssemblyPath(string repositoryRoot, string projectName)
    {
        var framework = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configuration = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        return Path.Combine(
            repositoryRoot,
            "src",
            projectName,
            "bin",
            configuration,
            framework,
            $"{projectName}.dll");
    }
}
