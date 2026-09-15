using Qvec.Core;

namespace Qvec.Core.Client.Tests.SourceGenFixModels.Collision.First
{
    public sealed class Product
    {
        [QvecIndexed]
        public string? Sku { get; init; }
    }
}

namespace Qvec.Core.Client.Tests.SourceGenFixModels.Collision.Second
{
    public sealed class Product
    {
        [QvecIndexed]
        public string? Sku { get; init; }
    }
}

namespace Qvec.Core.Client.Tests.SourceGenFixModels.Records
{
    public sealed record PositionalProduct([property: QvecIndexed] string Sku, [property: QvecIndexed] int Quantity, string Ignored);

    /// <summary>
    /// The attribute also targets parameters, so a positional record can be annotated without
    /// the <c>[property: ...]</c> prefix. The generator must treat both spellings identically.
    /// </summary>
    public sealed record BareParameterProduct([QvecIndexed] string Sku, [QvecIndexed] int Quantity, string Ignored);
}

namespace Qvec.Core.Client.Tests.SourceGenFixModels.DocumentedUsage
{
    public sealed class Product
    {
        [QvecIndexed]
        public string? Sku { get; init; }
    }
}

namespace Qvec.Core.Client.Tests.SourceGenFixModels.Values
{
    public enum ValueKind
    {
        Unknown,
        Special
    }

    public sealed class ValueTypeProduct
    {
        [QvecIndexed]
        public int Count { get; init; }

        [QvecIndexed]
        public bool Enabled { get; init; }

        [QvecIndexed]
        public DateTime Created { get; init; }

        [QvecIndexed]
        public ValueKind Kind { get; init; }

        [QvecIndexed]
        public decimal Ratio { get; init; }

        [QvecIndexed]
        public string? OptionalLabel { get; init; }
    }
}

namespace Qvec.Core.Client.Tests.SourceGenFixModels.Inheritance
{
    public class IndexedBase
    {
        [QvecIndexed]
        public string? BaseCode { get; init; }
    }

    public sealed class DerivedWithOwnIndexedProperty : IndexedBase
    {
        [QvecIndexed]
        public string? DerivedCode { get; init; }
    }
}
