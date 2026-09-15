namespace Qvec.Core
{
    /// <summary>
    /// Extraherar indexerade fältvärden från en instans av T.
    /// Implementeras av source-genererad kod.
    /// </summary>
    public interface IQvecFieldExtractor<in T>
    {
        /// <summary>
        /// Returnerar namnen på alla fält som <see cref="ExtractFields(T)"/> kan returnera.
        /// </summary>
        /// <remarks>
        /// QvecClient konsumerar extraktorer via den här instansen, så en static abstract-medlem
        /// skulle inte gå att anropa utan att även bära den konkreta genererade typen. ReadOnlySpan
        /// låter genererade extraktorer exponera en statiskt cachelagrad array utan per-anrop-
        /// allokeringar och utan att reflektera över T, vilket passar Native AOT. Nedärvda
        /// attribut kopieras inte in i en härledd typs extraktor; använd basextraktorn via
        /// kontravarians om basfält ska indexeras för härledda instanser.
        /// </remarks>
        System.ReadOnlySpan<string> IndexedFields => System.ReadOnlySpan<string>.Empty;

        /// <summary>
        /// Returnerar (fältnamn, värde)-par för alla [QvecIndexed]-properties.
        /// </summary>
        IEnumerable<(string Field, string Value)> ExtractFields(T item);
    }
}
