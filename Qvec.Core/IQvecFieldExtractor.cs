namespace Qvec.Core
{
    /// <summary>
    /// Extracts indexed field values from an instance of T.
    /// Implemented by source-generated code.
    /// </summary>
    public interface IQvecFieldExtractor<in T>
    {
        /// <summary>
        /// Returns the names of all fields that <see cref="ExtractFields(T)"/> can return.
        /// </summary>
        /// <remarks>
        /// QvecClient consumes extractors through this instance, so a static abstract member
        /// could not be called without also carrying the concrete generated type. ReadOnlySpan
        /// lets generated extractors expose a statically cached array without per-call
        /// allocations and without reflecting over T, which suits Native AOT. Inherited
        /// attributes are not copied into a derived type's extractor; use the base extractor via
        /// contravariance if base fields should be indexed for derived instances.
        /// </remarks>
        System.ReadOnlySpan<string> IndexedFields => System.ReadOnlySpan<string>.Empty;

        /// <summary>
        /// Returns (field name, value) pairs for all [QvecIndexed] properties.
        /// </summary>
        IEnumerable<(string Field, string Value)> ExtractFields(T item);
    }
}
