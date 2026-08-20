using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Inference;

public sealed class QueryCatalog
{
    private IReadOnlyDictionary<string, QueryIdentity> _queries = new Dictionary<string, QueryIdentity>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, QueryIdentity> Snapshot => Volatile.Read(ref _queries);

    public void Replace(IEnumerable<QueryIdentity> queries)
    {
        var snapshot = queries.ToDictionary(query => query.Id, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _queries, snapshot);
    }
}
