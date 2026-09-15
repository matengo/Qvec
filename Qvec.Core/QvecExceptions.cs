using System;

namespace Qvec.Core
{
    /// <summary>
    /// Base type for all Qvec-specific errors. Catch this to handle any failure
    /// originating from the database itself rather than from the runtime.
    /// </summary>
    public class QvecException : Exception
    {
        public QvecException(string message) : base(message) { }
        public QvecException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Thrown when the database has no free slots left. The capacity of a Qvec
    /// database is fixed at creation time via the <c>max</c> parameter.
    /// </summary>
    public sealed class QvecFullException : QvecException
    {
        public int MaxCount { get; }

        public QvecFullException(int maxCount)
            : base($"Database is full: capacity of {maxCount} entries is exhausted. " +
                   "Create the database with a larger 'max', or delete and vacuum existing entries.")
        {
            MaxCount = maxCount;
        }

        /// <summary>
        /// Used when a capacity other than the row count is exhausted, such as the metadata heap,
        /// where the row limit is not the meaningful number to report.
        /// </summary>
        public QvecFullException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Thrown when a supplied vector does not match the dimension the database was created with.
    /// </summary>
    public sealed class QvecDimensionException : QvecException
    {
        public int Expected { get; }
        public int Actual { get; }

        public QvecDimensionException(int expected, int actual, string paramName)
            : base($"Vector dimension mismatch for '{paramName}': database expects {expected} " +
                   $"dimensions but the supplied vector has {actual}.")
        {
            Expected = expected;
            Actual = actual;
        }
    }

    /// <summary>
    /// Thrown when a file is not a valid Qvec database, or its header is inconsistent
    /// with the parameters it is being opened with.
    /// </summary>
    public sealed class QvecFormatException : QvecException
    {
        public QvecFormatException(string message) : base(message) { }
    }
}
