namespace Qvec.Core
{
    /// <summary>
    /// Markerar en property som ska ingå i det inverterade indexet.
    /// Används av Qvec.SourceGen för att generera en IQvecFieldExtractor.
    /// </summary>
    /// <remarks>
    /// Parameter is allowed so that the attribute can be placed directly on a positional
    /// record's primary-constructor parameter, without the <c>[property: ...]</c> prefix.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
    public sealed class QvecIndexedAttribute : Attribute { }
}
