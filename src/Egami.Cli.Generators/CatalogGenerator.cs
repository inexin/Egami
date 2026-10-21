using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Egami.Cli.Generators;

/// <summary>
/// Writes the tool's step catalog and writer-settings catalog from Egami's public API at build time: every public
/// <c>RasterOps</c> method (and extension method on it) whose parameters the command line can spell becomes a step, and every
/// public property of each format's <c>WriterOptions</c> becomes a <c>--set fmt.key=value</c> key. Help text comes from the
/// referenced assemblies' XML documentation, passed as additional files. No reflection is left for run time.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class CatalogGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<string>> docs = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, token) => file.GetText(token)?.ToString() ?? string.Empty)
            .Collect();

        context.RegisterSourceOutput(context.CompilationProvider.Combine(docs), static (output, pair) =>
        {
            Docs documentation = Docs.Read(pair.Right);
            Compilation compilation = pair.Left;
            INamedTypeSymbol? ops = compilation.GetTypeByMetadataName("Egami.Operations.RasterOps");
            INamedTypeSymbol? writer = compilation.GetTypeByMetadataName("Egami.Formats.WriterOptions");
            if (ops is null || writer is null)
            {
                return;
            }

            TypeMap types = new(compilation);
            output.AddSource("StepCatalog.g.cs", SourceText.From(StepWriter.Write(compilation, ops, types, documentation), Encoding.UTF8));
            output.AddSource("WriterCatalog.g.cs", SourceText.From(SettingsWriter.Write(compilation, writer, types, documentation), Encoding.UTF8));
            output.AddSource("Choices.g.cs", SourceText.From(types.WriteChoices(), Encoding.UTF8));
        });
    }
}

/// <summary>How a parameter or property type is spelled on the command line, and how its text is parsed.</summary>
internal sealed class TypeMap
{
    private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);
    private readonly List<INamedTypeSymbol> _enums = [];

    public TypeMap(Compilation compilation)
    {
        Add(compilation, "Egami.Pixels.Rgba8", "ValueParsers.Color", "color");
        Add(compilation, "Egami.Point", "ValueParsers.Point", "x,y");
        Add(compilation, "Egami.Rect", "ValueParsers.Rect", "x,y,w,h");
        Add(compilation, "Egami.Size", "ValueParsers.Size", "WxH");
        Add(compilation, "System.Numerics.Vector2", "ValueParsers.Vector2", "x,y");
        Add(compilation, "Egami.Colors.ColorProfile", "ValueParsers.Profile", "profile");
        Add(compilation, "Egami.Raster", "ValueParsers.Image", "image");
    }

    /// <summary>The parser expression and display name for a type, or <see langword="null"/> when the command line cannot spell it.</summary>
    public (string Parser, string Display)? Find(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Int32: return ("ValueParsers.Int", "int");
            case SpecialType.System_Int64: return ("ValueParsers.Long", "integer");
            case SpecialType.System_Byte: return ("ValueParsers.Byte", "0-255");
            case SpecialType.System_UInt64: return ("ValueParsers.ULong", "integer");
            case SpecialType.System_Single: return ("ValueParsers.Float", "number");
            case SpecialType.System_Double: return ("ValueParsers.Double", "number");
            case SpecialType.System_Boolean: return ("ValueParsers.Bool", "bool");
            case SpecialType.System_String: return ("ValueParsers.Text", "text");
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            int index = _enums.FindIndex(e => SymbolEqualityComparer.Default.Equals(e, enumType));
            if (index < 0)
            {
                index = _enums.Count;
                _enums.Add(enumType);
            }

            return ($"Choices.Parse{index}", "choice");
        }

        if (type is INamedTypeSymbol { IsGenericType: true } list
            && list.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IReadOnlyList<T>"
            && list.TypeArguments[0].SpecialType == SpecialType.System_Int32)
        {
            return ("ValueParsers.IntList", "int,int,...");
        }

        return _named.TryGetValue(type.ToDisplayString(), out string? parser) ? (parser, Display(type)) : null;
    }

    /// <summary>The choice names of an enum the map has seen, kebab case, or <see langword="null"/> for other types.</summary>
    public string[]? Choices(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType ? Members(enumType).Select(m => Names.Kebab(m.Name)).ToArray() : null;
    }

    /// <summary>Writes the enum parsers: one name table, value table and parse method per enum used.</summary>
    public string WriteChoices()
    {
        StringBuilder text = new();
        text.AppendLine("// <auto-generated/>");
        text.AppendLine("#nullable enable");
        text.AppendLine("namespace Egami.Cli.Steps;");
        text.AppendLine();
        text.AppendLine("/// <summary>Parsers for the enums the steps and writer settings take, kebab-case names generated from Egami's public enums.</summary>");
        text.AppendLine("internal static class Choices");
        text.AppendLine("{");
        for (int i = 0; i < _enums.Count; i++)
        {
            INamedTypeSymbol type = _enums[i];
            List<IFieldSymbol> members = Members(type);
            bool flags = type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");
            string full = Names.Full(type);
            text.Append("    internal static readonly string[] Names").Append(i).Append(" = [")
                .Append(string.Join(", ", members.Select(m => Literals.String(Names.Kebab(m.Name))))).AppendLine("];");
            text.Append("    private static readonly long[] Values").Append(i).Append(" = [")
                .Append(string.Join(", ", members.Select(m => Convert.ToInt64(m.ConstantValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "L"))).AppendLine("];");
            text.Append("    internal static object Parse").Append(i).Append("(string text) => (").Append(full).Append(")")
                .Append(flags ? "ValueParsers.Flags" : "ValueParsers.Choice").Append("(text, Names").Append(i).Append(", Values").Append(i).AppendLine(");");
        }

        text.AppendLine("}");
        return text.ToString();
    }

    private static List<IFieldSymbol> Members(INamedTypeSymbol type)
    {
        return type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue && f.DeclaredAccessibility == Accessibility.Public).ToList();
    }

    private static string Display(ITypeSymbol type)
    {
        return type.ToDisplayString() switch
        {
            "Egami.Pixels.Rgba8" => "color",
            "Egami.Point" => "x,y",
            "Egami.Rect" => "x,y,w,h",
            "Egami.Size" => "WxH",
            "System.Numerics.Vector2" => "x,y",
            "Egami.Colors.ColorProfile" => "profile",
            "Egami.Raster" => "image",
            _ => type.Name,
        };
    }

    private void Add(Compilation compilation, string metadataName, string parser, string display)
    {
        if (compilation.GetTypeByMetadataName(metadataName) is { } type)
        {
            _named[type.ToDisplayString()] = parser;
        }

        _ = display;
    }
}

/// <summary>Writes StepCatalog.g.cs.</summary>
internal static class StepWriter
{
    public static string Write(Compilation compilation, INamedTypeSymbol ops, TypeMap types, Docs docs)
    {
        // Instance methods first, then extension methods from the referenced Egami assemblies, grouped by name.
        List<(IMethodSymbol Method, bool Extension)> methods = [];
        foreach (IMethodSymbol method in ops.GetMembers().OfType<IMethodSymbol>())
        {
            if (method is { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsGenericMethod: false }
                && SymbolEqualityComparer.Default.Equals(method.ReturnType, ops))
            {
                methods.Add((method, false));
            }
        }

        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols.Where(a => a.Name.StartsWith("Egami", StringComparison.Ordinal)).OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            foreach (INamedTypeSymbol type in AllTypes(assembly.GlobalNamespace).Where(t => t.IsStatic && t.DeclaredAccessibility == Accessibility.Public).OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal))
            {
                foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method is { IsExtensionMethod: true, DeclaredAccessibility: Accessibility.Public, IsGenericMethod: false }
                        && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, ops)
                        && SymbolEqualityComparer.Default.Equals(method.ReturnType, ops))
                    {
                        methods.Add((method, true));
                    }
                }
            }
        }

        StringBuilder text = new();
        text.AppendLine("// <auto-generated/>");
        text.AppendLine("#nullable enable");
        text.AppendLine("namespace Egami.Cli.Steps;");
        text.AppendLine();
        text.AppendLine("internal static partial class StepCatalog");
        text.AppendLine("{");
        text.AppendLine("    /// <summary>Creates the steps generated from Egami's public <c>RasterOps</c> methods.</summary>");
        text.AppendLine("    private static partial global::System.Collections.Generic.List<StepInfo> CreateGenerated()");
        text.AppendLine("    {");
        text.AppendLine("        return");
        text.AppendLine("        [");
        foreach (IGrouping<string, (IMethodSymbol Method, bool Extension)> group in methods.GroupBy(m => m.Method.Name).OrderBy(g => Names.Kebab(g.Key), StringComparer.Ordinal))
        {
            List<(IMethodSymbol Method, bool Extension, List<IParameterSymbol> Spelled)> overloads = [];
            foreach ((IMethodSymbol method, bool extension) in group)
            {
                List<IParameterSymbol> parameters = method.Parameters.Skip(extension ? 1 : 0).ToList();
                if (parameters.All(p => types.Find(p.Type) is not null || p.HasExplicitDefaultValue))
                {
                    overloads.Add((method, extension, parameters.Where(p => types.Find(p.Type) is not null).ToList()));
                }
            }

            if (overloads.Count == 0)
            {
                continue;
            }

            // The richest overload first, so a call that names every parameter of it binds there.
            overloads = overloads.OrderByDescending(o => o.Spelled.Count).ToList();
            IMethodSymbol first = overloads[0].Method;
            string summary = docs.Summary(first);
            text.Append("            new StepInfo(").Append(Literals.String(Names.Kebab(group.Key))).Append(", ")
                .Append(Literals.String(group.Key)).Append(", ").Append(Literals.String(summary)).AppendLine(",");
            text.AppendLine("            [");
            foreach ((IMethodSymbol method, _, List<IParameterSymbol> spelled) in overloads)
            {
                text.AppendLine("                new OverloadInfo(");
                text.AppendLine("                [");
                foreach (IParameterSymbol parameter in spelled)
                {
                    (string parser, string display) = types.Find(parameter.Type)!.Value;
                    string[]? choices = types.Choices(parameter.Type);
                    text.Append("                    new ParamInfo(").Append(Literals.String(Names.Kebab(parameter.Name))).Append(", ")
                        .Append(Literals.String(display)).Append(", ").Append(parser).Append(", ")
                        .Append(parameter.HasExplicitDefaultValue ? "false" : "true").Append(", ")
                        .Append(DefaultText(parameter)).Append(", ")
                        .Append(Literals.String(docs.Param(method, parameter.Name))).Append(", ")
                        .Append(choices is null ? "null" : "[" + string.Join(", ", choices.Select(Literals.String)) + "]")
                        .AppendLine("),");
                }

                text.AppendLine("                ]),");
            }

            text.AppendLine("            ],");
            text.AppendLine("            static (ops, overload, args) =>");
            text.AppendLine("            {");
            text.AppendLine("                switch (overload)");
            text.AppendLine("                {");
            for (int o = 0; o < overloads.Count; o++)
            {
                (IMethodSymbol method, bool extension, List<IParameterSymbol> spelled) = overloads[o];
                List<string> arguments = [];
                foreach (IParameterSymbol parameter in method.Parameters.Skip(extension ? 1 : 0))
                {
                    int index = spelled.IndexOf(parameter);
                    string full = Names.Full(parameter.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
                    string underlying = parameter.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
                        ? Names.Full(n.TypeArguments[0])
                        : full.TrimEnd('?');
                    string fallback = parameter.HasExplicitDefaultValue ? DefaultLiteral(parameter) : "default!";
                    arguments.Add(index < 0
                        ? $"{parameter.Name}: {fallback}"
                        : $"{parameter.Name}: args[{index}] is {{ }} v{o}x{index} ? ({underlying})v{o}x{index} : {fallback}");
                }

                string call = extension
                    ? $"{Names.Full(method.ContainingType)}.{method.Name}(ops{(arguments.Count > 0 ? ", " : string.Empty)}{string.Join(", ", arguments)})"
                    : $"ops.{method.Name}({string.Join(", ", arguments)})";
                text.Append("                    case ").Append(o.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(call).AppendLine("; break;");
            }

            text.AppendLine("                }");
            text.AppendLine("            }),");
        }

        text.AppendLine("        ];");
        text.AppendLine("    }");
        text.AppendLine("}");
        return text.ToString();
    }

    private static string DefaultText(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue || parameter.ExplicitDefaultValue is null)
        {
            return "null";
        }

        object value = parameter.ExplicitDefaultValue;
        if (parameter.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        {
            return EnumOrValueText(n.TypeArguments[0], value);
        }

        return EnumOrValueText(parameter.Type, value);
    }

    private static string EnumOrValueText(ITypeSymbol type, object value)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            long number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            IFieldSymbol? member = enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Convert.ToInt64(f.ConstantValue, CultureInfo.InvariantCulture) == number);
            return Literals.String(member is null ? number.ToString(CultureInfo.InvariantCulture) : Names.Kebab(member.Name));
        }

        return Literals.String(value switch
        {
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        });
    }

    private static string DefaultLiteral(IParameterSymbol parameter)
    {
        string full = Names.Full(parameter.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
        object? value = parameter.ExplicitDefaultValue;
        if (value is null)
        {
            return $"default({full})";
        }

        ITypeSymbol type = parameter.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : parameter.Type;
        if (type.TypeKind == TypeKind.Enum)
        {
            return $"({Names.Full(type)})({Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)})";
        }

        return value switch
        {
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            ulong u => u.ToString(CultureInfo.InvariantCulture) + "UL",
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            string s => Literals.String(s),
            IFormattable f => $"({full})({f.ToString(null, CultureInfo.InvariantCulture)})",
            _ => $"default({full})",
        };
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol space)
    {
        foreach (INamedTypeSymbol type in space.GetTypeMembers())
        {
            yield return type;
        }

        foreach (INamespaceSymbol child in space.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol type in AllTypes(child))
            {
                yield return type;
            }
        }
    }
}

/// <summary>Writes WriterCatalog.g.cs.</summary>
internal static class SettingsWriter
{
    public static string Write(Compilation compilation, INamedTypeSymbol writer, TypeMap types, Docs docs)
    {
        List<INamedTypeSymbol> formats = [];
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols.Where(a => a.Name.StartsWith("Egami", StringComparison.Ordinal)))
        {
            Collect(assembly.GlobalNamespace, writer, formats);
        }

        formats = formats.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        StringBuilder text = new();
        text.AppendLine("// <auto-generated/>");
        text.AppendLine("#nullable enable");
        text.AppendLine("namespace Egami.Cli.Steps;");
        text.AppendLine();
        text.AppendLine("internal static partial class WriterCatalog");
        text.AppendLine("{");
        text.AppendLine("    /// <summary>Creates the writer settings generated from each format's public <c>WriterOptions</c> type.</summary>");
        text.AppendLine("    private static partial global::System.Collections.Generic.List<WriterInfo> CreateGenerated()");
        text.AppendLine("    {");
        text.AppendLine("        return");
        text.AppendLine("        [");
        for (int i = 0; i < formats.Count; i++)
        {
            INamedTypeSymbol type = formats[i];
            string full = Names.Full(type);
            string prefix = type.Name.Substring(0, type.Name.Length - "WriterOptions".Length);
            text.Append("            new WriterInfo(").Append(Literals.String(prefix.ToLowerInvariant())).Append(", ")
                .Append(Literals.String(docs.Summary(type))).Append(", static () => ").Append(full).Append(".Default, Build").Append(i)
                .AppendLine(",");
            text.AppendLine("            [");
            foreach ((string key, IPropertySymbol property, string access) in Settings(type, types, string.Empty, "(" + "(" + full + ")o)"))
            {
                (string _, string display) = types.Find(property.Type)!.Value;
                string[]? choices = types.Choices(property.Type);
                text.Append("                new SettingInfo(").Append(Literals.String(key)).Append(", ").Append(Literals.String(display)).Append(", ")
                    .Append(Literals.String(docs.Summary(property))).Append(", ")
                    .Append(choices is null ? "null" : "[" + string.Join(", ", choices.Select(Literals.String)) + "]")
                    .Append(", static o => ").Append(access).AppendLine("),");
            }

            text.AppendLine("            ]),");
        }

        text.AppendLine("        ];");
        text.AppendLine("    }");
        List<string> nestedMethods = [];
        for (int i = 0; i < formats.Count; i++)
        {
            INamedTypeSymbol type = formats[i];
            string full = Names.Full(type);
            text.AppendLine();
            text.Append("    private static global::Egami.Formats.WriterOptions Build").Append(i).AppendLine("(SettingValues s)");
            text.AppendLine("    {");
            text.Append("        ").Append(full).Append(" d = ").Append(full).AppendLine(".Default;");
            text.Append("        return ").Append(Initializer(type, types, "d", string.Empty, "        ", "Build" + i, nestedMethods)).AppendLine(";");
            text.AppendLine("    }");
        }

        foreach (string method in nestedMethods)
        {
            text.AppendLine();
            text.Append(method);
        }

        text.AppendLine("}");
        return text.ToString();
    }

    /// <summary>The settable properties of a type and its bases, most derived last, base properties first.</summary>
    private static List<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        List<IPropertySymbol> result = [];
        Stack<INamedTypeSymbol> chain = new();
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            chain.Push(t);
        }

        while (chain.Count > 0)
        {
            foreach (IPropertySymbol property in chain.Pop().GetMembers().OfType<IPropertySymbol>())
            {
                if (property is { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsIndexer: false, SetMethod.DeclaredAccessibility: Accessibility.Public, GetMethod: not null }
                    && property.Name != "EqualityContract")
                {
                    result.Add(property);
                }
            }
        }

        return result;
    }

    /// <summary>A nested options object the settings reach into with a dotted key, such as <c>palette.max-colors</c>.</summary>
    private static INamedTypeSymbol? Nested(IPropertySymbol property)
    {
        return property.Type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type
            && type.ContainingNamespace.ToDisplayString().StartsWith("Egami", StringComparison.Ordinal)
            && type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
            && Properties(type).Count > 0
            ? type
            : null;
    }

    private static IEnumerable<(string Key, IPropertySymbol Property, string Access)> Settings(INamedTypeSymbol type, TypeMap types, string prefix, string owner)
    {
        foreach (IPropertySymbol property in Properties(type))
        {
            string key = prefix + Names.Kebab(property.Name);
            if (types.Find(property.Type) is not null)
            {
                yield return (key, property, $"{owner}.{property.Name}");
            }
            else if (prefix.Length == 0 && Nested(property) is { } nested)
            {
                string access = property.NullableAnnotation == NullableAnnotation.Annotated ? $"{owner}.{property.Name}?" : $"{owner}.{property.Name}";
                foreach ((string Key, IPropertySymbol Property, string Access) inner in Settings(nested, types, key + ".", access))
                {
                    yield return inner;
                }
            }
        }
    }

    private static string Initializer(INamedTypeSymbol type, TypeMap types, string from, string prefix, string indent, string owner, List<string> nestedMethods)
    {
        StringBuilder text = new();
        text.Append("new ").Append(Names.Full(type)).AppendLine();
        text.Append(indent).AppendLine("{");
        foreach (IPropertySymbol property in Properties(type))
        {
            string key = prefix + Names.Kebab(property.Name);
            string current = $"{from}.{property.Name}";
            text.Append(indent).Append("    ").Append(property.Name).Append(" = ");
            if (types.Find(property.Type) is { } found)
            {
                text.Append("s.Get(").Append(Literals.String(key)).Append(", ").Append(found.Parser).Append(", ").Append(current).Append(')');
            }
            else if (prefix.Length == 0 && Nested(property) is { } nested)
            {
                string method = owner + property.Name;
                string nestedType = Names.Full(nested);
                string start = property.NullableAnnotation == NullableAnnotation.Annotated ? $"({current} ?? new {nestedType}())" : current;
                text.Append("s.HasPrefix(").Append(Literals.String(key + ".")).Append(") ? ").Append(method).Append("(s, ").Append(start).Append(") : ").Append(current);
                string body = Initializer(nested, types, "b", key + ".", "        ", method, nestedMethods);
                const char NewLine = '\n';
                nestedMethods.Add($"    private static {nestedType} {method}(SettingValues s, {nestedType} b){NewLine}    {{{NewLine}        return {body};{NewLine}    }}{NewLine}");
            }
            else
            {
                text.Append(current);
            }

            text.AppendLine(",");
        }

        text.Append(indent).Append('}');
        return text.ToString();
    }

    private static void Collect(INamespaceSymbol space, INamedTypeSymbol writer, List<INamedTypeSymbol> found)
    {
        foreach (INamedTypeSymbol type in space.GetTypeMembers())
        {
            if (type is { DeclaredAccessibility: Accessibility.Public, IsAbstract: false }
                && type.Name.EndsWith("WriterOptions", StringComparison.Ordinal)
                && type.Name.Length > "WriterOptions".Length
                && InheritsFrom(type, writer)
                && type.GetMembers("Default").OfType<IPropertySymbol>().Any(p => p.IsStatic))
            {
                found.Add(type);
            }
        }

        foreach (INamespaceSymbol child in space.GetNamespaceMembers())
        {
            Collect(child, writer, found);
        }
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, baseType))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Names as the command line spells them.</summary>
internal static class Names
{
    /// <summary>A type's fully qualified name with <c>global::</c>, keywords for the special types.</summary>
    public static string Full(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>Turns <c>ContrastStretch</c> into <c>contrast-stretch</c> and <c>perFrame</c> into <c>per-frame</c>.</summary>
    public static string Kebab(string name)
    {
        StringBuilder text = new(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool afterLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (afterLower || acronymEnd)
                {
                    text.Append('-');
                }

                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}

/// <summary>C# literals.</summary>
internal static class Literals
{
    public static string String(string value)
    {
        StringBuilder text = new(value.Length + 2);
        text.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < ' ' || c > '~')
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
