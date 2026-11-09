using System.Globalization;
using PlantOps.Application.AI;

namespace PlantOps.AI;

internal sealed class InvestigationModelSubjects
{
    private readonly Dictionary<string, string> aliasesByRef = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> refsByAlias = new(StringComparer.Ordinal);

    public InvestigationModelSubjects(IReadOnlyList<InvestigationEvidence> evidence)
    {
        Subjects = InvestigationSubjects.FromEvidence(evidence).Select((subject, index) =>
        {
            var alias = "S" + (index + 1).ToString(CultureInfo.InvariantCulture);
            aliasesByRef.Add(subject.Ref, alias);
            refsByAlias.Add(alias, subject.Ref);
            return subject with
            {
                Ref = alias
            };
        }).ToArray();
        FindingRefs = InvestigationSubjects.FindingRefs(evidence).Select(reference => aliasesByRef[reference]).ToArray();
        AllowedAliases = refsByAlias.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public InvestigationSubject[] Subjects
    {
        get;
    }

    public string[] FindingRefs
    {
        get;
    }

    public IReadOnlySet<string> AllowedAliases
    {
        get;
    }

    public string? AliasFor(Guid resourceId) => aliasesByRef.GetValueOrDefault(resourceId.ToString());

    public string Resolve(string alias) => refsByAlias[alias];
}
