using PlantOps.Application.AI;

namespace PlantOps.AI;

// State assertions are checked against typed server evidence, never inferred from prose.
internal static class InvestigationStateContract
{
    public const string Instructions = "\nStructured evidence states are authoritative. When the output schema includes stateClaims, return a claim for every cited evidence with a supplied state, copying availability/currentTrust/currentAnomaly exactly; null means not applicable. Otherwise omit stateClaims. "
        + "Availability concerns the bounded query only: Partial is an excerpt, Empty is a successful query with no records, Unavailable is a failed read, Unknown is unspecified. It does not establish measurement trust, complete history or equipment health. "
        + "Use stateClaims for state assertions; prose explains their scope and limitations, never overrides them. Input omission is not unavailability. Missing maintenance resolution is not missing maintenance records. "
        + "A conditional escalation describes a future contingency, not current missing evidence. A reference to an existing shutdown authorization procedure is an escalation, not an instruction to shut equipment down. "
        + "Stopping an inference is not stopping equipment. Saying safety cannot be confirmed is not asserting safety. Prefer clear read-only review wording. Do not claim unavailable raw samples can be reconstructed.";

    public const string Schema = """
        {"type":"array","maxItems":40,"items":{
          "type":"object","additionalProperties":false,
          "required":["evidenceId","availability","currentTrust","currentAnomaly"],
          "properties":{
            "evidenceId":{"type":"string","minLength":1,"maxLength":100},
            "availability":{"type":"string","enum":["Available","Partial","Empty","Unavailable","Unknown"]},
            "currentTrust":{"type":["string","null"],"enum":["Trusted","Partial","Unverifiable",null]},
            "currentAnomaly":{"type":["string","null"],"enum":["Present","NotFound","Insufficient",null]}
          }
        }}
        """;

    public static void Validate(InvestigationResult result, IReadOnlyList<InvestigationEvidence> evidence,
        bool requireClaims = false)
    {
        var ledger = evidence.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var claims = result.StateClaims ?? [];
        if (requireClaims && (result.StateClaims == null || result.EvidenceIds
            .Where(id => ledger[id].State != null).Except(claims.Select(x => x.EvidenceId), StringComparer.Ordinal).Any()))
        {
            throw new LlmException(LlmFailure.InvalidResponse, validationRule: "missing-state-claims");
        }
        foreach (var claim in claims)
        {
            var state = ledger[claim.EvidenceId].State;
            if (state == null || claim.Availability != state.Availability
                || claim.CurrentTrust != state.CurrentTrust || claim.CurrentAnomaly != state.CurrentAnomaly)
            {
                throw new LlmException(LlmFailure.InvalidResponse, validationRule: "contradictory-state-claim");
            }
        }
    }
}
