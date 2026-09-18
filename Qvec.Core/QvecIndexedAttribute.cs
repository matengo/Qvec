namespace Qvec.Core
{
    /// <summary>
    /// Marks a property that should be included in the inverted index.
    /// Used by Qvec.SourceGen to generate an IQvecFieldExtractor.
    /// </summary>
    /// <remarks>
    /// Parameter is allowed so that the attribute can be placed directly on a positional
    /// record's primary-constructor parameter, without the <c>[property: ...]</c> prefix.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
    public sealed class QvecIndexedAttribute : Attribute { }
}
