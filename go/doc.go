// Package agentexperience is a Go core of AgentExperience that implements the AgentExperience.NET language-neutral
// contract 1.15 (the repository's contract directory).
//
// Pre-release. It finalizes a captured run into a stored Experience Record, renders the injected Historical
// Reference block, derives the task text, classifies errors, and computes the text-search parameters and the
// ranking. For an agent integration it also has the verifiers, the tool-result classifiers and the identity an
// invocation runs under. The postgres subpackage writes records to and searches them in a PostgreSQL schema the
// .NET migrator applied, the inmemory subpackage does the same in memory, and the maf subpackage wires all of it
// into Microsoft Agent Framework for Go; each is its own module, so this one requires no module at all and has no
// Microsoft Agent Framework dependency.
//
// The lessons view (contract 1.7, Lessons) lists, read-only and in the caller's own scope, what has been learned: each
// lesson's status, confidence and checks, the block text an agent would be shown for it (RenderAlone), why retrieval
// would not use it when it would not (Status, BelowConfidenceFloor, ModelAuthored, OverByteBudget or Unrenderable; the
// .NET Expired and ContentUnconfirmed do not exist here, which has no maximum-age policy and no provenance signing),
// and when it was last injected. Contract 1.14 renders a page as .NET's HTML (RenderLessonsHTML, LessonsHTMLHandler)
// and exports the lessons the holdout measured as helping (Lessons.ExportMeasured, MarkdownLessonFileSink).
//
// The injection holdout (contract 1.8, AssignHoldout) assigns a captured run to the arm .NET would, so a Go agent
// withholds the block on the same share of runs and records the rows the .NET benefit estimate reads. The benefit
// estimate (contract 1.9, EstimateBenefit) reads those rows back, a page at a time, and gives the .NET estimate's
// numbers: overall, per lesson and, split by the run's model (ModelMetadataKey), per model. The benefit policy (contract
// 1.10, BenefitPolicy) acts on it: BenefitView is what one estimate means for an invocation, and SelectCandidates the
// selection .NET's context provider runs, so a lesson measured Hurts is omitted (MeasuredHarmful) and, on request, the
// lessons measured Helps go first. The lessons view shows each lesson's measured benefit (IncludeMeasuredBenefit). The
// injection options (contract 1.13) add the receiving agent's capability gate (ReceivingAgent, NewCapabilityGate,
// GatedWindow, SelectOptions.Gate) and the model-authored policy (ModelAuthoredLessonPolicy). Contract 1.14 adds the
// reflector seam (Reflector, FinalizeContextWith) and the content guard screening applies to a model-authored
// reflection (ScreenWith, ScreeningRefusalError); the maf module's ChatClientReflector is the model-backed reflector.
//
// The confidence engine (contract 1.15) computes how a record's reuse confidence moves when evidence about reusing
// it arrives, as .NET does: ConfidenceScore (the default rule, (1 + S) / (2 + S + F)), ApplyConfidenceEvidence,
// StatusAfter and IndependenceKeyFor; the ConfidenceEngine seam, checked and recorded by ConfidenceEngineRule; and
// AssessmentTokenIssuer, which mints the token a human assessment carries. It only computes: applying an update to a
// stored record is not part of this package yet.
//
// The contract is generated from .NET, where a string is UTF-16: every length, cut and limit here is counted in
// UTF-16 code units, never in bytes or runes. Whitespace, word characters and ignoring case are .NET's, not Go's
// (Go's regexp \s is ASCII only and its \b is ASCII words, and strings.EqualFold and regexp's (?i) fold the long s
// and the Kelvin sign to ASCII letters). JSON numbers in tool-call arguments are kept exactly as written.
//
// Nothing here logs: no record content leaves the package except through the values it returns.
package agentexperience

// ContractVersion is the contract version this package implements.
const ContractVersion = "1.19"

// SupportedContractVersions are the contract versions this package passes: every fixture of each.
var SupportedContractVersions = []string{"1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7", "1.8", "1.9", "1.10", "1.11", "1.12", "1.13", "1.14", "1.15", "1.16", "1.17", "1.18", "1.19"}
