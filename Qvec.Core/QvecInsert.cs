namespace Qvec.Core;

/// <summary>
/// One entry in a batch passed to <see cref="QvecDatabase.AddEntries"/>.
/// </summary>
/// <param name="Vector">The embedding. Must have the database's dimension.</param>
/// <param name="Metadata">Caller-supplied metadata string stored with the entry.</param>
/// <param name="ExternalId">
/// Optional stable id. When an entry with this id already exists, or the same id appears
/// earlier in the batch, the entry is skipped and the existing id is returned in its place,
/// exactly as <see cref="QvecDatabase.AddEntry"/> behaves.
/// </param>
public readonly record struct QvecInsert(float[] Vector, string Metadata, Guid? ExternalId = null);
