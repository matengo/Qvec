namespace Qvec.Core
{
    /// <summary>
    /// Markerar en property som ska ingå i det inverterade indexet.
    /// Används av Qvec.SourceGen för att generera en IQvecFieldExtractor.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class QvecIndexedAttribute : Attribute { }
}
