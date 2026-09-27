using AgentExperience.Abstractions;

namespace AgentExperience.Core.Confidence;

/// <summary>
/// What an <see cref="IExperienceConfidenceEngine"/> scores: the record as the library read it, and the
/// evidence counters to score.
/// </summary>
/// <remarks>
/// When evidence is applied, the counters are the ones <em>after</em> this evidence: the record's own
/// <see cref="ExperienceRecord.SupportingValidations"/> and <see cref="ExperienceRecord.Contradictions"/>
/// are still the prior values. When a confidence read recomputes a score, they are the stored counters
/// less the evidence the read excludes. Treat the record as data: it may carry content a host engine
/// must not log.
/// </remarks>
/// <param name="Record">The record as it was read.</param>
/// <param name="SupportingValidations">The independent accepted supporting validations to score. Never negative.</param>
/// <param name="Contradictions">The independent accepted contradictions to score. Never negative.</param>
public sealed record ExperienceConfidenceInput(ExperienceRecord Record, int SupportingValidations, int Contradictions);
