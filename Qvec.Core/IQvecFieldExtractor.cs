namespace Qvec.Core
{
    /// <summary>
    /// Extraherar indexerade fältvärden från en instans av T.
    /// Implementeras av source-genererad kod.
    /// </summary>
    public interface IQvecFieldExtractor<in T>
    {
        /// <summary>
        /// Returnerar (fältnamn, värde)-par för alla [QvecIndexed]-properties.
        /// </summary>
        IEnumerable<(string Field, string Value)> ExtractFields(T item);
    }
}
