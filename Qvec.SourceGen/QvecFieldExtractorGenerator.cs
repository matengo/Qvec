using System.Collections.Immutable;
using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Qvec.SourceGen
{
    [Generator]
    public class QvecFieldExtractorGenerator : IIncrementalGenerator
    {
        private const string QvecIndexedAttribute = "Qvec.Core.QvecIndexedAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var indexedAttributedMembers = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    QvecIndexedAttribute,
                    predicate: static (node, _) => node is PropertyDeclarationSyntax or ParameterSyntax,
                    transform: static (ctx, _) => CreateIndexedMemberInfo(ctx.TargetSymbol))
                .Where(static info => info is not null)
                .Select(static (info, _) => info!.Value)
                .Collect();

            var indexedRecordParameters = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is ParameterSyntax { AttributeLists.Count: > 0 },
                    transform: static (ctx, _) => CreateIndexedRecordParameterInfo(ctx))
                .Where(static info => info is not null)
                .Select(static (info, _) => info!.Value)
                .Collect();

            context.RegisterSourceOutput(indexedAttributedMembers.Combine(indexedRecordParameters), static (spc, indexedMembers) =>
            {
                var members = indexedMembers.Left.Concat(indexedMembers.Right).ToArray();
                if (members.Length == 0) return;

                var grouped = members.GroupBy(p => p.TypeIdentity).OrderBy(g => g.Key, StringComparer.Ordinal);

                foreach (var group in grouped)
                {
                    var first = group.First();
                    var props = group
                        .GroupBy(g => g.PropertyName)
                        .Select(g => g.First())
                        .OrderBy(g => g.PropertyName, StringComparer.Ordinal)
                        .ToArray();
                    var source = GenerateExtractor(first.TypeNamespace, first.TypeName, first.TypeReference, props);
                    spc.AddSource($"{first.HintName}FieldExtractor.g.cs", SourceText.From(source, Encoding.UTF8));
                }
            });
        }

        private static IndexedPropertyInfo? CreateIndexedMemberInfo(ISymbol symbol)
        {
            switch (symbol)
            {
                case IPropertySymbol property:
                    return CreateIndexedMemberInfo(property.ContainingType, property.Name, property.Type, property.NullableAnnotation);

                case IParameterSymbol parameter
                    when parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType.IsRecord: true } constructor:
                    return CreateIndexedMemberInfo(constructor.ContainingType, parameter.Name, parameter.Type, parameter.NullableAnnotation);

                default:
                    return null;
            }
        }

        private static IndexedPropertyInfo? CreateIndexedRecordParameterInfo(GeneratorSyntaxContext context)
        {
            var parameterSyntax = (ParameterSyntax)context.Node;
            if (parameterSyntax.Parent is not ParameterListSyntax { Parent: RecordDeclarationSyntax })
            {
                return null;
            }

            if (!HasQvecIndexedAttribute(context.SemanticModel, parameterSyntax))
            {
                return null;
            }

            if (context.SemanticModel.GetDeclaredSymbol(parameterSyntax) is not IParameterSymbol parameter ||
                parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            {
                return null;
            }

            return CreateIndexedMemberInfo(constructor.ContainingType, parameter.Name, parameter.Type, parameter.NullableAnnotation);
        }

        private static bool HasQvecIndexedAttribute(SemanticModel semanticModel, ParameterSyntax parameterSyntax)
        {
            foreach (var attribute in parameterSyntax.AttributeLists.SelectMany(list => list.Attributes))
            {
                var attributeType = semanticModel.GetTypeInfo(attribute).Type ??
                    (semanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol)?.ContainingType;

                if (attributeType?.ToDisplayString() == QvecIndexedAttribute)
                {
                    return true;
                }
            }

            return false;
        }

        private static IndexedPropertyInfo? CreateIndexedMemberInfo(
            INamedTypeSymbol containingType,
            string propertyName,
            ITypeSymbol propertyType,
            NullableAnnotation nullableAnnotation)
        {
            var extractorAccessibility = GetExtractorAccessibility(containingType);
            if (extractorAccessibility == null)
            {
                return null;
            }

            return new IndexedPropertyInfo(
                TypeNamespace: containingType.ContainingNamespace.IsGlobalNamespace
                    ? ""
                    : containingType.ContainingNamespace.ToDisplayString(),
                TypeName: containingType.Name,
                TypeIdentity: containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeReference: containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                HintName: SanitizeHintName(containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                PropertyName: propertyName,
                PropertyAccessor: EscapeIdentifier(propertyName),
                FieldNameLiteral: SymbolDisplay.FormatLiteral(propertyName, quote: true),
                ValueLocalName: "__qvecValue_" + SanitizeIdentifier(propertyName),
                IsValueType: propertyType.IsValueType,
                IsNullable: !propertyType.IsValueType || nullableAnnotation == NullableAnnotation.Annotated,
                ExtractorAccessibility: extractorAccessibility);
        }

        private static string GenerateExtractor(string ns, string typeName, string typeReference, IndexedPropertyInfo[] properties)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Globalization;");
            sb.AppendLine("using Qvec.Core;");
            sb.AppendLine();

            var extractorNs = string.IsNullOrEmpty(ns) ? "Qvec.Generated" : ns;
            sb.AppendLine($"namespace {extractorNs}");
            sb.AppendLine("{");
            sb.AppendLine($"    {properties[0].ExtractorAccessibility} sealed class {typeName}FieldExtractor : IQvecFieldExtractor<{typeReference}>");
            sb.AppendLine("    {");
            sb.AppendLine($"        private static readonly string[] s_indexedFields = new[] {{ {string.Join(", ", properties.Select(p => p.FieldNameLiteral))} }};");
            sb.AppendLine();
            sb.AppendLine("        public ReadOnlySpan<string> IndexedFields => s_indexedFields;");
            sb.AppendLine();
            sb.AppendLine($"        public IEnumerable<(string Field, string Value)> ExtractFields({typeReference} item)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (item == null) yield break;");

            foreach (var prop in properties)
            {
                if (prop.IsNullable)
                {
                    sb.AppendLine($"            var {prop.ValueLocalName} = item.{prop.PropertyAccessor};");
                    sb.AppendLine($"            if ({prop.ValueLocalName} != null)");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                yield return ({prop.FieldNameLiteral}, string.Format(CultureInfo.InvariantCulture, \"{{0}}\", {prop.ValueLocalName}));");
                    sb.AppendLine("            }");
                }
                else
                {
                    sb.AppendLine($"            yield return ({prop.FieldNameLiteral}, string.Format(CultureInfo.InvariantCulture, \"{{0}}\", item.{prop.PropertyAccessor}));");
                }
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string EscapeIdentifier(string identifier)
        {
            return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;
        }

        private static string SanitizeHintName(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            return sb.ToString();
        }

        private static string SanitizeIdentifier(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            return sb.ToString();
        }

        private static string? GetExtractorAccessibility(INamedTypeSymbol type)
        {
            var current = type;
            var isPublic = true;
            while (current != null)
            {
                switch (current.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                        break;

                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                        isPublic = false;
                        break;

                    default:
                        return null;
                }

                current = current.ContainingType;
            }

            return isPublic ? "public" : "internal";
        }

        private record struct IndexedPropertyInfo(
            string TypeNamespace,
            string TypeName,
            string TypeIdentity,
            string TypeReference,
            string HintName,
            string PropertyName,
            string PropertyAccessor,
            string FieldNameLiteral,
            string ValueLocalName,
            bool IsValueType,
            bool IsNullable,
            string ExtractorAccessibility);
    }
}
