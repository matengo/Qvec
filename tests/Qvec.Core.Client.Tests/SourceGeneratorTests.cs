using System.Globalization;
using Qvec.Core;
using Qvec.Core.Client.Tests.GeneratedModels;

namespace Qvec.Core.Client.Tests;

public sealed class SourceGeneratorTests
{
    [Fact]
    public void NamedNamespace_ExtractorIsGeneratedInContainingNamespace()
    {
        var extractor = new NamedIndexedThingFieldExtractor();
        var item = new NamedIndexedThing
        {
            Text = "hello",
            Number = 42,
            LongNumber = 4_200_000_000,
            DoubleNumber = 1.5,
            DecimalNumber = 12.25m,
            Flag = true,
            Kind = GeneratedKind.Primary,
            Missing = null
        };

        var fields = extractor.ExtractFields(item).ToDictionary(x => x.Field, x => x.Value);

        Assert.Equal("hello", fields[nameof(NamedIndexedThing.Text)]);
        Assert.Equal("42", fields[nameof(NamedIndexedThing.Number)]);
        Assert.Equal("4200000000", fields[nameof(NamedIndexedThing.LongNumber)]);
        Assert.Equal(1.5.ToString(CultureInfo.InvariantCulture), fields[nameof(NamedIndexedThing.DoubleNumber)]);
        Assert.Equal(12.25m.ToString(CultureInfo.InvariantCulture), fields[nameof(NamedIndexedThing.DecimalNumber)]);
        Assert.Equal("True", fields[nameof(NamedIndexedThing.Flag)]);
        Assert.Equal("Primary", fields[nameof(NamedIndexedThing.Kind)]);
        Assert.False(fields.ContainsKey(nameof(NamedIndexedThing.Missing)));

        AssertIndexedFieldsMatchExtractedFields(
            extractor,
            new NamedIndexedThing
            {
                Text = "hello",
                Number = 42,
                LongNumber = 4_200_000_000,
                DoubleNumber = 1.5,
                DecimalNumber = 12.25m,
                Flag = true,
                Kind = GeneratedKind.Primary,
                Missing = "present"
            });
    }

    [Fact]
    public void SameShortTypeNameInDifferentNamespaces_GeneratesBothExtractors()
    {
        var firstExtractor = new SourceGenFixModels.Collision.First.ProductFieldExtractor();
        var secondExtractor = new SourceGenFixModels.Collision.Second.ProductFieldExtractor();

        var firstField = Assert.Single(firstExtractor.ExtractFields(new SourceGenFixModels.Collision.First.Product { Sku = "first" }));
        var secondField = Assert.Single(secondExtractor.ExtractFields(new SourceGenFixModels.Collision.Second.Product { Sku = "second" }));

        Assert.Equal(("Sku", "first"), firstField);
        Assert.Equal(("Sku", "second"), secondField);
        AssertIndexedFieldsMatchExtractedFields(
            firstExtractor,
            new SourceGenFixModels.Collision.First.Product { Sku = "first" });
        AssertIndexedFieldsMatchExtractedFields(
            secondExtractor,
            new SourceGenFixModels.Collision.Second.Product { Sku = "second" });
    }

    [Fact]
    public void PositionalRecordParameterWithIndexedAttribute_GeneratesIndexedField()
    {
        var extractor = new SourceGenFixModels.Records.PositionalProductFieldExtractor();

        var fields = extractor.ExtractFields(new SourceGenFixModels.Records.PositionalProduct("ABC-123", 12, "ignored"))
            .ToDictionary(x => x.Field, x => x.Value);

        Assert.Equal("ABC-123", fields["Sku"]);
        Assert.Equal("12", fields["Quantity"]);
        Assert.False(fields.ContainsKey("Ignored"));
        AssertIndexedFieldsMatchExtractedFields(
            extractor,
            new SourceGenFixModels.Records.PositionalProduct("ABC-123", 12, "ignored"));
    }

    [Fact]
    public void PositionalRecordParameterWithBareIndexedAttribute_GeneratesIndexedField()
    {
        var extractor = new SourceGenFixModels.Records.BareParameterProductFieldExtractor();

        var fields = extractor.ExtractFields(new SourceGenFixModels.Records.BareParameterProduct("ABC-123", 12, "ignored"))
            .ToDictionary(x => x.Field, x => x.Value);

        Assert.Equal("ABC-123", fields["Sku"]);
        Assert.Equal("12", fields["Quantity"]);
        Assert.False(fields.ContainsKey("Ignored"));
        AssertIndexedFieldsMatchExtractedFields(
            extractor,
            new SourceGenFixModels.Records.BareParameterProduct("ABC-123", 12, "ignored"));
    }

    [Fact]
    public void ModelNamespace_ExtractorSupportsDocumentedUnqualifiedUsage()
    {
        var extractor = new SourceGenFixModels.DocumentedUsage.ProductFieldExtractor();

        var field = Assert.Single(extractor.ExtractFields(new SourceGenFixModels.DocumentedUsage.Product { Sku = "same-namespace" }));

        Assert.Equal(("Sku", "same-namespace"), field);
        AssertIndexedFieldsMatchExtractedFields(
            extractor,
            new SourceGenFixModels.DocumentedUsage.Product { Sku = "same-namespace" });
    }

    [Fact]
    public void NonNullableValueTypesUseInvariantCultureAndNullReferenceValuesAreSkipped()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sv-SE");
            var extractor = new SourceGenFixModels.Values.ValueTypeProductFieldExtractor();

            var fields = extractor.ExtractFields(new SourceGenFixModels.Values.ValueTypeProduct
                {
                    Count = 7,
                    Enabled = true,
                    Created = new DateTime(2026, 9, 14, 22, 2, 51, DateTimeKind.Utc),
                    Kind = SourceGenFixModels.Values.ValueKind.Special,
                    Ratio = 12.25m,
                    OptionalLabel = null
                })
                .ToDictionary(x => x.Field, x => x.Value);

            Assert.Equal("7", fields["Count"]);
            Assert.Equal("True", fields["Enabled"]);
            Assert.Equal(new DateTime(2026, 9, 14, 22, 2, 51, DateTimeKind.Utc).ToString(CultureInfo.InvariantCulture), fields["Created"]);
            Assert.Equal("Special", fields["Kind"]);
            Assert.Equal(12.25m.ToString(CultureInfo.InvariantCulture), fields["Ratio"]);
            Assert.False(fields.ContainsKey("OptionalLabel"));

            AssertIndexedFieldsMatchExtractedFields(
                extractor,
                new SourceGenFixModels.Values.ValueTypeProduct
                {
                    Count = 7,
                    Enabled = true,
                    Created = new DateTime(2026, 9, 14, 22, 2, 51, DateTimeKind.Utc),
                    Kind = SourceGenFixModels.Values.ValueKind.Special,
                    Ratio = 12.25m,
                    OptionalLabel = "present"
                });
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void GlobalNamespace_ExtractorIsGeneratedInQvecGeneratedNamespace()
    {
        var extractor = new Qvec.Generated.GlobalIndexedThingFieldExtractor();

        var field = Assert.Single(extractor.ExtractFields(new GlobalIndexedThing { Label = "global" }));

        Assert.Equal(nameof(GlobalIndexedThing.Label), field.Field);
        Assert.Equal("global", field.Value);
        AssertIndexedFieldsMatchExtractedFields(extractor, new GlobalIndexedThing { Label = "global" });
    }

    [Fact]
    public void TypeWithSeveralIndexedProperties_ExtractorReturnsEveryIndexedField()
    {
        var extractor = new SeveralIndexedThingFieldExtractor();

        var fields = extractor.ExtractFields(new SeveralIndexedThing
            {
                One = "one",
                Two = "two",
                Three = "three"
            })
            .ToDictionary(x => x.Field, x => x.Value);

        Assert.Equal(new[] { "One", "Three", "Two" }, fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("one", fields[nameof(SeveralIndexedThing.One)]);
        Assert.Equal("two", fields[nameof(SeveralIndexedThing.Two)]);
        Assert.Equal("three", fields[nameof(SeveralIndexedThing.Three)]);
        AssertIndexedFieldsMatchExtractedFields(
            extractor,
            new SeveralIndexedThing
            {
                One = "one",
                Two = "two",
                Three = "three"
            });
    }

    [Fact]
    public void ManualTypeWithNoIndexedProperties_HasEmptyIndexedFields()
    {
        var extractor = new NoIndexedCatalogItemExtractor();
        var item = new NoIndexedCatalogItem { Title = "not indexed" };

        Assert.Empty(extractor.IndexedFields.ToArray());
        Assert.Empty(extractor.ExtractFields(item));
        AssertIndexedFieldsMatchExtractedFields(extractor, item);
    }

    [Fact]
    public void InheritedIndexedProperties_AreNotCopiedIntoDerivedExtractor()
    {
        var extractor = new SourceGenFixModels.Inheritance.DerivedWithOwnIndexedPropertyFieldExtractor();
        var item = new SourceGenFixModels.Inheritance.DerivedWithOwnIndexedProperty
        {
            BaseCode = "base",
            DerivedCode = "derived"
        };

        Assert.Equal(new[] { "DerivedCode" }, extractor.IndexedFields.ToArray());
        Assert.Equal(new[] { ("DerivedCode", "derived") }, extractor.ExtractFields(item).ToArray());
        AssertIndexedFieldsMatchExtractedFields(extractor, item);
    }

    [Fact]
    public void BaseExtractorCanIndexInheritedPropertiesForDerivedInstances()
    {
        IQvecFieldExtractor<SourceGenFixModels.Inheritance.DerivedWithOwnIndexedProperty> extractor =
            new SourceGenFixModels.Inheritance.IndexedBaseFieldExtractor();
        var item = new SourceGenFixModels.Inheritance.DerivedWithOwnIndexedProperty
        {
            BaseCode = "base",
            DerivedCode = "derived"
        };

        Assert.Equal(new[] { "BaseCode" }, extractor.IndexedFields.ToArray());
        Assert.Equal(new[] { ("BaseCode", "base") }, extractor.ExtractFields(item).ToArray());
        AssertIndexedFieldsMatchExtractedFields(extractor, item);
    }

    [Fact]
    public void TypeWithNoIndexedProperties_DoesNotGetAnExtractor()
    {
        var assemblyName = typeof(GlobalNoIndexedThing).Assembly.GetName().Name;

        var generatedType = Type.GetType($"Qvec.Generated.GlobalNoIndexedThingFieldExtractor, {assemblyName}", throwOnError: false);

        Assert.Null(generatedType);
    }

    private static void AssertIndexedFieldsMatchExtractedFields<T>(IQvecFieldExtractor<T> extractor, T item)
    {
        var extractedFieldNames = extractor.ExtractFields(item).Select(x => x.Field).ToArray();

        Assert.Equal(extractedFieldNames, extractor.IndexedFields.ToArray());
    }
}
