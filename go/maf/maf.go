// Package maf wires AgentExperience into Microsoft Agent Framework for Go (github.com/microsoft/agent-framework-go):
// capture, verify, store and inject lessons, with one call on the agent's configuration.
//
//	store, err := inmemory.New()
//	if err != nil {
//		return err
//	}
//	identity, err := ae.DevelopmentIdentity(ae.DevelopmentOptions{}) // development and tests only
//	if err != nil {
//		return err
//	}
//	config := agent.Config{Name: "triage-ticket", Tools: []tool.Tool{runTests}}
//	if err := maf.UseAgentExperience(&config, maf.Options{Store: store, Identity: identity,
//		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}); err != nil {
//		return err
//	}
//	triage := openaiprovider.NewChatCompletionsAgent(client, openaiprovider.AgentConfig{Config: config, Model: model})
//
// (the package's Example compiles this).
//
// The Go SDK builds an agent from its configuration once (agent.New, or a provider's constructor), so the one call
// changes the configuration before the agent is built. What it adds:
//
//   - Capture, as agent middleware (outermost, so it wraps the history and context providers) plus function
//     invocation middleware (outermost). Each invocation is one run of one attempt, streaming or not: every tool call
//     with its arguments, its result and its error (an error the tool returned, recorded by its type name only; a
//     panic, recorded by its value's type and re-raised; or a returned call the classifier named an error class
//     for). When the invocation ends, Verify decides from the host's own facts; a verdict is finalized through the
//     core (the deterministic lesson, the derived IDs) and stored. Without Verify nothing is stored.
//
//   - Injection, as a context provider. It derives the task text from the invocation's messages (the contract's
//     rule), searches the store in the identity's scope, ranks with the contract's weights, renders the Historical
//     Reference block with BlockOptions, and adds it as one user-role message (MessageRole system sends it as a
//     system message) just before the new input, stamped AdditionalProperties[HistoricalReferenceKey] = true.
//
//   - Injection tracking (contract 1.4). Once the block is handed over, a store that records injections (an
//     ae.InjectionRecorder: the PostgreSQL store, the in-memory store) is told which records it delivered, in a
//     goroutine bounded by InjectionRecordTimeout, so the .NET lessons view shows when each lesson was last injected.
//     It never delays or fails the invocation; the outcome is reported through OnInjectionRecorded, and
//     ContextProviderOf(config).Close awaits the records still running, for a host shutting down.
//
//   - Semantic retrieval (contract 1.6), with Options.Embedder. Injection then runs the text and the vector channel
//     together, merges them as the contract says and ranks as before; every stored record's summary is embedded in
//     the background (a failure is reported as StageIndex and never fails the capture). Without an embedder, or when
//     the vector channel fails, the injection answers from text alone and says why (OnVectorFallback). To register
//     one, implement ae.Embedder (ModelID, Dimension, Embed) over your provider's client and set Options.Embedder; no
//     embedding model is a dependency of this module.
//
//   - The injection holdout (contract 1.8), with Options.Holdout. On a deterministic share (Rate) of captured runs whose
//     block names a record, the block is built and then withheld: no message, no exposure, no injection record, and
//     OnWithheld is told what was withheld. Every assigned run, either arm, is recorded in the background through a
//     store that is an ae.HoldoutRecorder (the PostgreSQL store, the in-memory store), so the .NET benefit estimate
//     compares the runs of every language. Off by default (rate 0): nothing then changes.
//
//   - Acting on the benefit estimate (contract 1.10), with Options.Benefit. A lesson the holdout measured Hurts is not
//     injected (InjectionResult.Omitted names it, MeasuredHarmful), a scope measured Hurts gets no block, and with
//     PreferMeasuredHelpful the lessons measured Helps go first. The estimate is cached per scope and principal and
//     refreshed in a goroutine (the benefit_refresh operation), never on the request path. Off by default.
//
//   - The lessons view (contract 1.7), read-only. LessonsOf(&config) (or the provider's Lessons method, or NewLessons for
//     the options alone) lists what has been learned in a scope: each lesson's status, confidence and checks, the
//     block text an agent would be shown for it, why retrieval would not use it, and when it was last injected, from
//     the same store, block options and model-authored flag the injection uses. It reads only, in the caller's own
//     scope and authorization, and renders no HTML.
//
// Nothing here returns an error or panics into the agent: a store, identity, classifier or verifier failure means no
// block, or no stored record, and is reported through OnInjectionFailure or OnCaptureFailure. Only the invocation's
// own errors and cancellation, and the tool's own errors and panics, reach the caller, unchanged. A panic through the
// invocation (the provider's, a middleware's, or one raised in the caller's own loop over the updates, which runs
// inside the iterator) ends its run Failed, recorded as panic(<type>), and goes on unchanged.
package maf

import (
	"context"
	"errors"
	"fmt"
	"math"
	"os"
	"reflect"
	"runtime"
	"runtime/debug"
	"slices"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// modulePath is this module's path, whose version the build records.
const modulePath = "github.com/fabbrik/AgentExperience.NET/go/maf"

// develVersion is the version recorded when the build knows none (an untagged module built from its directory).
const develVersion = "0.0.0-dev"

// Version is this module's version as the build recorded it (runtime/debug.ReadBuildInfo: the main module's, or
// the dependency's), else "0.0.0-dev". It is recorded as the provenance source version of every run captured here.
func Version() string { return moduleVersion(debug.ReadBuildInfo()) }

func moduleVersion(info *debug.BuildInfo, ok bool) string {
	if !ok || info == nil {
		return develVersion
	}
	usable := func(m *debug.Module) (string, bool) {
		if m == nil || m.Path != modulePath || m.Version == "" || m.Version == "(devel)" {
			return "", false
		}
		if m.Replace != nil && m.Replace.Version != "" {
			return m.Replace.Version, true
		}
		return m.Version, true
	}
	if version, ok := usable(&info.Main); ok {
		return version
	}
	for _, dependency := range info.Deps {
		if version, ok := usable(dependency); ok {
			return version
		}
	}
	return develVersion
}

// sourceVersion is the version recorded; the cross-language test pins it, so its golden file does not change with
// every version.
var sourceVersion = Version()

// HistoricalReferenceKey is the AdditionalProperties key that stamps the injected message.
const HistoricalReferenceKey = "AgentExperience.HistoricalReference"

// SourceID is the context provider's source ID, which the injected message's Source names.
const SourceID = "agentexperience"

// ProvenanceSource is the provenance source every run captured here is recorded with.
const ProvenanceSource = "agentexperience.maf"

// DefaultTaskID is the task ID runs are captured under when none is given and the agent's name keeps nothing.
const DefaultTaskID = "default"

// DefaultFinalizationTimeout is how long an invocation's end waits for finalization by default: .NET's
// FinalizationTimeout.
const DefaultFinalizationTimeout = 5 * time.Second

// The errors recorded for an invocation or a tool call that did not end on its own.
const (
	// CanceledError is recorded when the caller's context was canceled.
	CanceledError = "context.Canceled"
	// DeadlineExceededError is recorded when the caller's context passed its deadline.
	DeadlineExceededError = "context.DeadlineExceeded"
	// StoppedError is recorded for an invocation whose caller stopped reading its updates before it ended.
	StoppedError = "agent.RunStopped"
)

// ErrAlreadyWired is UseAgentExperience called a second time on one configuration.
var ErrAlreadyWired = errors.New("maf: UseAgentExperience was already called on this agent configuration")

// CaptureStage names where capture failed.
type CaptureStage string

// The capture stages.
const (
	StageResolveIdentity          CaptureStage = "ResolveIdentity"
	StageStartRun                 CaptureStage = "StartRun"
	StageRecordExposure           CaptureStage = "RecordExposure"
	StageToolCall                 CaptureStage = "ToolCall"
	StageToolResultClassification CaptureStage = "ToolResultClassification"
	StageAppendAttempt            CaptureStage = "AppendAttempt"
	StageCompleteRun              CaptureStage = "CompleteRun"
	StageVerify                   CaptureStage = "Verify"
	StageFinalize                 CaptureStage = "Finalize"
	StageStore                    CaptureStage = "Store"
	// StageIndex is the background embedding write after a record was stored (contract 1.6): the record stays
	// stored, only its vector is missing.
	StageIndex CaptureStage = "Index"
)

// CaptureFailure is a capture problem, reported once per stage per run. Reason never quotes record content; Err is
// the error (or the recovered panic, as an error), when there was one.
type CaptureFailure struct {
	Stage  CaptureStage
	Reason string
	// RunID is the run's ID, once it started.
	RunID *ae.UUID
	Err   error
}

// InjectionFailure is why an invocation got no block: the reason (never record content), and the error, when there
// was one.
type InjectionFailure struct {
	Reason string
	Err    error
}

// InjectionResult is what one injection delivered: the records in the block, best first, the block's size in UTF-8
// bytes, whether the search returned more candidates than the ranking considers, and, when the vector channel did not
// contribute (contract 1.6), why: nil when it did.
type InjectionResult struct {
	// Outcome is the inject operation's outcome, as .NET's InjectionOutcome names it: Injected; NothingToInject (no
	// task text, or every ranked record was omitted: Omitted says why, for example MeasuredHarmful); Withheld (in
	// what OnWithheld is told); or, with nothing injected, Skipped (no identity, or ResolveRequest returned nil),
	// Failed, RetrievalFailed, RetrievalTimedOut or RetrievalDenied (contract 1.13: every attempt is reported, as .NET's
	// OnContextInjected is; OnInjectionFailure still says why one failed). Unlike .NET's ExperienceInjectionResult it
	// carries no CorrelationId or Failure: the request's correlation ID is on the inject span, and a failure's reason
	// and error go to OnInjectionFailure.
	Outcome        string
	ExperienceIDs  []ae.UUID
	ByteCount      int
	Truncated      bool
	VectorFallback *ae.VectorFallback
	// WithheldExperienceIDs are the records a held-out invocation's block named and did not deliver (contract 1.8):
	// set only in what OnWithheld is told, where ExperienceIDs is empty and ByteCount 0.
	WithheldExperienceIDs []ae.UUID
	// Omitted are the ranked records that were not put in the block, and why, in the order .NET reports them:
	// MeasuredHarmful (contract 1.10), ModelAuthored, ToolUnavailable and RiskClassExceeded (the capability gate, contract
	// 1.13), OverRecordLimit, HostDenied (the host's DecideInjection, contract 1.13) and OverByteBudget.
	Omitted []ae.OmittedRecord
	// Excluded are the candidates ranking left out, and why (contract 1.12, .NET's Excluded): IneligibleStatus,
	// ModelAuthored or Expired (RetrievalPolicy.MaxAge). Not a complete account: what the search filtered never shows.
	Excluded []ae.Exclusion
}

// HoldoutOptions are the injection holdout (contract 1.8). The zero value is off.
//
// Who takes part: only an invocation captured by the same UseAgentExperience call (whose run started), whose built
// block names at least one record. The arm is ae.AssignHoldout(run ID, Salt, Rate), a pure function, so every
// invocation of a run lands in the same arm, and the arm is the one the .NET and Python integrations assign. A
// held-out invocation gets no block (the block is built first, so what is withheld is exactly what would have been
// delivered): no message, no exposure on the run, no injection record; the inject outcome is Withheld and OnWithheld
// is told. A treatment invocation is injected exactly as without the holdout. Both arms are recorded in the
// background (the record_holdout operation, OnHoldoutRecorded), and never delay or fail the invocation. This port has
// no withdrawal notices, so a held-out invocation never owes any.
type HoldoutOptions struct {
	// Rate is the share of eligible runs whose block is withheld, from 0 (off, the default) to 1 (every one). NaN, a
	// negative rate or one above 1 is refused by UseAgentExperience.
	Rate float64
	// Salt is mixed into every assignment, so two experiments over the same runs draw different arms;
	// ae.DefaultHoldoutSalt when empty. (Go cannot tell an empty salt from an unset one, so empty means the default
	// here, where .NET and Python refuse an explicitly empty salt.) Changing it re-draws every run.
	Salt string
}

// IdentityContext is what an identity resolver is given: the agent and the caller's session. Never model output.
type IdentityContext struct {
	Agent   *agent.Agent
	Session *agent.Session
}

// Options configure UseAgentExperience (and NewContextProvider).
type Options struct {
	// Store is required: the in-memory store (the inmemory module) for development, or the PostgreSQL store.
	Store ae.Store
	// Identity is who every invocation runs for: ae.DevelopmentIdentity for development and tests, or one from your
	// own authentication. Exactly one of Identity and ResolveIdentity is given.
	Identity ae.Identity
	// ResolveIdentity resolves the identity per invocation from your own authentication; nil (with no error) leaves
	// the invocation neither captured nor injected. A development identity it returns is checked every time.
	ResolveIdentity func(ctx context.Context, identity IdentityContext) (*ae.Identity, error)
	// Verify decides from your own facts whether a run is stored (see ae.ToolSucceeded and the others); without it
	// nothing is stored.
	Verify ae.Verifier
	// Reflector writes the lesson of a verified run (contract 1.14): the deterministic reflector when nil. Opt in to a
	// model with NewChatClientReflector; its call runs inside FinalizationTimeout, so raise that above the reflector's
	// own Timeout. A reflector that fails, or whose reflection screening refuses, stores the run Quarantined, with no
	// lesson (FinalizedExperience.ReflectionFailure, ScreeningRefusal).
	Reflector ae.Reflector
	// ClassifyToolResult marks a returned tool call as failed (see ae.ExitCode and ae.HTTPStatus).
	ClassifyToolResult ae.ToolResultClassifier
	// TaskID is the task ID runs are captured under; when empty, the agent's name reduced to an identifier, else
	// DefaultTaskID.
	TaskID string
	// Sanitization is ae.DefaultSanitization() when zero: only allowlisted argument values are kept.
	Sanitization ae.Sanitization
	// BlockOptions are the block's layout and limits (the contract's defaults when zero).
	BlockOptions ae.BlockOptions
	// Environment is the environment runs are recorded under; DefaultEnvironment() when nil.
	Environment *ae.EnvironmentFingerprint
	// Model is the model id the agent's chat client runs on (for example openaiprovider.AgentConfig.Model): Microsoft
	// Agent Framework for Go does not expose it on the built agent, so the host passes it. Every captured run records
	// it, normalized as ae.NormalizeModel does, under ae.ModelMetadataKey in its environment metadata, for the benefit
	// estimate's split by model; nothing when empty or blank, or when Environment already sets that key (the host's
	// value wins). It is bookkeeping only: never a precondition, never in the block, never ranked on.
	Model string
	// PreferredEnvironment is the metadata ranking prefers.
	PreferredEnvironment map[string]string
	// ExcludeModelAuthored leaves model-authored lessons out of the search and the ranking: the same as
	// ModelAuthoredLessons Exclude (either one excludes).
	ExcludeModelAuthored bool
	// ModelAuthoredLessons is .NET's ModelAuthoredLessonPolicy (contract 1.13): Include (when empty) injects a
	// model-authored lesson inside the fixed Authored:/End authored: fence; Exclude asks retrieval to leave them out
	// and omits any that arrives (ae.OmittedModelAuthored, no detail), before the record limit, so it takes no slot.
	ModelAuthoredLessons ae.ModelAuthoredLessonPolicy
	// ReceivingAgent is .NET's ReceivingAgentCapabilities (contract 1.13): what the agent receiving the block can and
	// may do. A record whose verified approach calls a tool it lacks, or one above its MaxRiskClass, is omitted
	// (ae.OmittedToolUnavailable, ae.OmittedRiskClassExceeded, no detail) before the record limit, so it takes no slot;
	// while the gate can reject and a request names its own Limit, retrieval is asked for a wider window. With
	// UseRunTools the tool check reads the tools the SDK hands the context providers (the configuration's Tools and
	// the run's agent.WithTool options, and any an earlier context provider added). nil gates nothing. Validated and
	// copied by UseAgentExperience.
	ReceivingAgent *ae.ReceivingAgent
	// ResolveRequest is .NET's ResolveRequest / ResolveRequestAsync (contract 1.13): called once per invocation the
	// identity resolved for, before retrieval, it returns what to retrieve. nil (the default) retrieves the derived
	// task text with PreferredEnvironment. A nil request with a nil error skips the invocation (inject outcome
	// Skipped); an error or a panic injects nothing (OnInjectionFailure, outcome Failed), and the invocation runs; the
	// caller's own cancellation (an error while ctx is done) propagates from Invoking. A request retrieval refuses (a
	// blank or overlong task text, a Limit outside 1..CandidateLimit) is RetrievalFailed. No timeout is put around it.
	ResolveRequest func(ctx context.Context, rc RequestContext) (*InjectionRequest, error)
	// DecideInjection is .NET's DecideInjection (contract 1.13): asked about each selected record, in order, before the
	// block is rendered. It can only deny: a denial omits the record as ae.OmittedHostDenied with the decision's
	// Reason as its detail (ae.HostDeniedDetail when empty), and it fails closed: the zero InjectionDecision, or a
	// panic, denies. A denied record has taken its slot. Synchronous, as .NET's.
	DecideInjection func(DecisionContext) InjectionDecision
	// MessageRole is the injected message's role: message.RoleUser (when empty) or message.RoleSystem.
	MessageRole message.Role
	// DisableInjection captures without injecting.
	DisableInjection bool
	// OnCaptureFailure, OnInjectionFailure, OnInjected, OnFinalized and OnInjectionRecorded are told what happened;
	// each is optional, and a panic in one is recovered and ignored. OnInjected is told every invocation that
	// injects (Outcome Injected) and, as .NET reports it, every one that searched or derived nothing to inject
	// (Outcome NothingToInject, with the omissions: a scope measured Hurts shows here). OnInjectionRecorded is called
	// from the recording's own goroutine.
	OnCaptureFailure    func(CaptureFailure)
	OnInjectionFailure  func(InjectionFailure)
	OnInjected          func(InjectionResult)
	OnFinalized         func(ae.FinalizedExperience)
	OnInjectionRecorded func(InjectionRecordResult)
	// Holdout is the injection holdout (contract 1.8); off when zero.
	Holdout HoldoutOptions
	// Benefit is acting on the holdout's benefit estimate (contract 1.10, .NET's ExperienceBenefitPolicy); off when
	// zero. With Enabled, a lesson measured Hurts is omitted (InjectionResult.Omitted, ae.OmittedMeasuredHarmful), a
	// scope measured Hurts gets no block, and with PreferMeasuredHelpful the lessons measured Helps go first. The
	// estimate is cached per scope and principal and refreshed in a goroutine, never on the request path: the first
	// invocation of a key is injected as with the policy off. A zero duration or minimum means its default; a negative
	// one, or a RefreshTimeout over a day, is refused by UseAgentExperience even with the policy off. The holdout runs
	// exactly as without it. The lessons view reads its measured benefit over the same Window and MinimumRunsPerArm.
	Benefit ae.BenefitPolicy
	// OnWithheld is told, for a held-out invocation, what its block withheld (InjectionResult.WithheldExperienceIDs);
	// OnHoldoutRecorded how recording each assignment ended, from the recording's own goroutine. Each is optional, and a
	// panic in one is recovered and ignored.
	OnWithheld        func(InjectionResult)
	OnHoldoutRecorded func(HoldoutRecordResult)
	// Clock is ae.Now when nil.
	Clock func() ae.Instant
	// NewID is ae.NewRandomUUID when nil.
	NewID func() ae.UUID
	// Embedder (contract 1.6) turns text into a vector, for hybrid retrieval and for embedding each stored record.
	// With one, injection runs the text and the vector channel together (a store that is an ae.EmbeddingIndex serves
	// it: the in-memory store, and the PostgreSQL store with pgvector), merges them and ranks as before, and every
	// stored record's summary is embedded in the background. Without one, or when the vector channel fails, the
	// injection answers from text alone and says why (OnVectorFallback, InjectionResult.VectorFallback). The
	// embedder's calls honour ctx, are bounded by VectorTimeout, and at most MaxVectorCalls run at once, abandoned
	// ones included.
	Embedder ae.Embedder
	// MinVectorRelevance, when set (0 to 1), drops vector candidates whose relevance is below it; text candidates
	// never are. When set, it replaces RetrievalPolicy.MinimumVectorRelevance (as .NET's one-call setup lets
	// MinimumSemanticSimilarity replace the policy's floor).
	MinVectorRelevance *float64
	// RetrievalPolicy is .NET's RetrievalPolicy (contract 1.12): the retrieval's Timeout (the text search and the
	// vector channel together: past it, even when only the vector channel is still running, the invocation gets no
	// block and the inject outcome RetrievalTimedOut; VectorTimeout is a fallback only when strictly shorter), the
	// search's confidence floor, candidate limit and minimum of matched terms, the expiry (MaxAge, from a record's
	// last lifecycle activity), the recency half-life, the abandoned-search cap and the vector floor. The zero policy
	// is .NET's RetrievalPolicy.Default; a zero field means its default (a zero MaxAge, no expiry), and a negative or
	// otherwise invalid one is refused by UseAgentExperience (an *ae.PolicyError naming the .NET option).
	RetrievalPolicy ae.RetrievalPolicy
	// ConfidenceDecay is .NET's ConfidenceDecayPolicy (contract 1.12): the confidence component fades by the lesson's
	// age (from CreatedAt) at its domain's half-life. Ranking only: it never excludes. nil decays nothing. Copied
	// when UseAgentExperience is called.
	ConfidenceDecay *ae.ConfidenceDecayPolicy
	// VectorTimeout bounds the vector channel of one injection in all (embedding the task text, then searching):
	// past it the injection answers from text alone and reports ProviderUnavailable or VectorSearchFailed.
	// DefaultVectorTimeout when 0.
	VectorTimeout time.Duration
	// OnVectorFallback is told, once per injection, why the vector channel did not contribute (NotConfigured,
	// ProviderUnavailable, ModelMismatch, DimensionMismatch, VectorSearchFailed); optional, and a panic in it is
	// recovered and ignored. Its detail never quotes record or vector content.
	OnVectorFallback func(ae.VectorFallback)
	// FinalizationTimeout bounds how long an invocation's end waits for finalization (append, verify, finalize,
	// store), on its own clock, never the caller's context: past it the work is abandoned and reported, and the
	// invocation returns. DefaultFinalizationTimeout when 0.
	FinalizationTimeout time.Duration
	// InjectionRecordTimeout bounds how long recording one injected block may take in the background, all chunks
	// together: past it the write is abandoned and reported InjectionOutcomeUnknown. The invocation never waits for
	// it. DefaultInjectionRecordTimeout when 0.
	InjectionRecordTimeout time.Duration
}

// DefaultEnvironment is the environment a run is recorded under by default: this host, this Go runtime, this
// operating system and architecture.
func DefaultEnvironment() ae.EnvironmentFingerprint {
	host, err := os.Hostname()
	if err != nil || host == "" {
		host = "unknown"
	}
	return ae.EnvironmentFingerprint{
		HostName:        host,
		RuntimeVersion:  runtime.Version(),
		OperatingSystem: runtime.GOOS,
		Metadata:        map[string]string{"ProcessArchitecture": runtime.GOARCH},
	}
}

// settings are the validated options capture and injection share.
type settings struct {
	Options
	environment ae.EnvironmentFingerprint
	// vectorSlots bound the embedder and vector-store calls in flight, abandoned ones included.
	vectorSlots chan struct{}
	// index counts the background embedding writes started and not yet ended.
	index tracker
	// benefit is the cache the benefit policy acts on (contract 1.10); nil with the policy off.
	benefit *benefitCache
	// retrieval and decay are the resolved retrieval policy and confidence decay (contract 1.12; decay nil for none).
	retrieval ae.RetrievalPolicy
	decay     *ae.ConfidenceDecayPolicy
	// gate is the receiving agent's capability gate, validated and copied (contract 1.13); nil for none.
	gate *ae.CapabilityGate
	// measured are the memory providers UseMeasuredMemory wrapped on this configuration (contract 1.11), whose
	// records the context provider's Close waits for too.
	measuredMu sync.Mutex
	measured   []*MeasuredMemory
}

// addMeasured registers a wrapper whose records Close waits for.
func (s *settings) addMeasured(w *MeasuredMemory) {
	s.measuredMu.Lock()
	defer s.measuredMu.Unlock()
	s.measured = append(s.measured, w)
}

// waitMeasured waits for every registered wrapper's records, or for ctx to be done.
func (s *settings) waitMeasured(ctx context.Context) error {
	s.measuredMu.Lock()
	wrappers := slices.Clone(s.measured)
	s.measuredMu.Unlock()
	for _, w := range wrappers {
		if err := w.Close(ctx); err != nil {
			return err
		}
	}
	return nil
}

func newSettings(options Options) (*settings, error) {
	if options.Store == nil {
		return nil, errors.New("maf: a store is required: the in-memory store for development, or the PostgreSQL store")
	}
	identitySet := !reflect.DeepEqual(options.Identity, ae.Identity{})
	if options.ResolveIdentity == nil {
		if !identitySet {
			return nil, errors.New("maf: an identity is required: ae.DevelopmentIdentity for development and tests, " +
				"or ResolveIdentity from your own authentication")
		}
		if err := options.Identity.Validate(); err != nil {
			return nil, err
		}
	} else if identitySet {
		return nil, errors.New("maf: give Identity or ResolveIdentity, not both")
	}
	// The sanitization is checked as capture checks it when a run starts, so a bad one fails here, not every run.
	if _, err := ae.StartRun(ae.CapturedRun{RunID: ae.UUID{15: 1}, TaskID: DefaultTaskID}, options.Sanitization,
		ae.CaptureLimits{}); err != nil {
		return nil, fmt.Errorf("maf: the sanitization: %w", err)
	}
	if options.Environment != nil {
		if err := validateEnvironment(*options.Environment); err != nil {
			return nil, err
		}
	}
	if options.TaskID != "" && ae.IsNullOrWhiteSpace(options.TaskID) {
		return nil, errors.New("maf: the task ID must not be blank; leave it empty to use the agent's name")
	}
	if err := options.BlockOptions.Validate(); err != nil {
		return nil, err
	}
	if options.BlockOptions.MaxBytes != 0 && options.BlockOptions.MaxBytes <= ae.BlockOverheadBytes {
		// As .NET's ExperienceInjectionLimits refuses it: a budget no record could ever fit in (0 is the default).
		return nil, fmt.Errorf("maf: the block's byte budget must exceed the block's fixed header and footer (%d bytes), "+
			"or no record could ever fit", ae.BlockOverheadBytes)
	}
	switch options.MessageRole {
	case "":
		options.MessageRole = message.RoleUser
	case message.RoleUser, message.RoleSystem:
	default:
		return nil, errors.New("maf: the message role must be user or system")
	}
	if options.FinalizationTimeout < 0 {
		return nil, errors.New("maf: the finalization timeout must not be negative")
	}
	if options.FinalizationTimeout == 0 {
		options.FinalizationTimeout = DefaultFinalizationTimeout
	}
	if options.InjectionRecordTimeout < 0 {
		return nil, errors.New("maf: the injection record timeout must not be negative")
	}
	if options.InjectionRecordTimeout == 0 {
		options.InjectionRecordTimeout = DefaultInjectionRecordTimeout
	}
	if options.VectorTimeout < 0 {
		return nil, errors.New("maf: the vector timeout must not be negative")
	}
	if options.VectorTimeout == 0 {
		options.VectorTimeout = DefaultVectorTimeout
	}
	if options.MinVectorRelevance != nil && !(*options.MinVectorRelevance >= 0 && *options.MinVectorRelevance <= 1) {
		return nil, errors.New("maf: the minimum vector relevance must be a number from 0 to 1")
	}
	retrievalPolicy := options.RetrievalPolicy
	if options.MinVectorRelevance != nil {
		// As .NET's one-call setup: the integration's floor replaces the policy's.
		retrievalPolicy.MinimumVectorRelevance = options.MinVectorRelevance
	}
	resolvedPolicy, err := retrievalPolicy.Resolved()
	if err != nil {
		return nil, fmt.Errorf("maf: %w", err)
	}
	var resolvedDecay *ae.ConfidenceDecayPolicy
	if options.ConfidenceDecay != nil {
		decay, err := options.ConfidenceDecay.Resolved()
		if err != nil {
			return nil, fmt.Errorf("maf: %w", err)
		}
		resolvedDecay = &decay
	}
	if math.IsNaN(options.Holdout.Rate) || options.Holdout.Rate < 0 || options.Holdout.Rate > 1 {
		return nil, errors.New("maf: the holdout rate must be a number from 0 to 1")
	}
	if err := options.Benefit.Validate(); err != nil {
		return nil, fmt.Errorf("maf: %w", err)
	}
	if err := options.ModelAuthoredLessons.Validate(); err != nil {
		return nil, fmt.Errorf("maf: %w", err)
	}
	if options.ExcludeModelAuthored {
		options.ModelAuthoredLessons = ae.ModelAuthoredExclude
	}
	if options.ModelAuthoredLessons == ae.ModelAuthoredExclude {
		options.ExcludeModelAuthored = true
	} else {
		options.ModelAuthoredLessons = ae.ModelAuthoredInclude
	}
	gate, err := ae.NewCapabilityGate(options.ReceivingAgent)
	if err != nil {
		return nil, fmt.Errorf("maf: %w", err)
	}
	options.ReceivingAgent = nil // copied into gate; the host's value is never read again
	if options.Holdout.Salt == "" {
		options.Holdout.Salt = ae.DefaultHoldoutSalt
	}
	if options.Clock == nil {
		options.Clock = ae.Now
	}
	if options.NewID == nil {
		options.NewID = ae.NewRandomUUID
	}
	s := &settings{Options: options, vectorSlots: make(chan struct{}, MaxVectorCalls), retrieval: resolvedPolicy,
		decay: resolvedDecay, gate: gate}
	if options.Environment != nil {
		s.environment = *options.Environment
	} else {
		s.environment = DefaultEnvironment()
	}
	s.environment = ae.EnvironmentWithModel(s.environment, ae.NormalizeModel(options.Model))
	if options.Benefit.Enabled {
		s.benefit = newBenefitCache(options.Store, options.Benefit, options.Clock)
	}
	return s, nil
}

// validateEnvironment refuses an environment no record should carry: a blank host name, runtime version or
// operating system, a blank application version or metadata key, or text that is not valid UTF-8 (PostgreSQL
// refuses it).
func validateEnvironment(environment ae.EnvironmentFingerprint) error {
	texts := []string{environment.HostName, environment.RuntimeVersion, environment.OperatingSystem}
	for _, text := range texts {
		if ae.IsNullOrWhiteSpace(text) {
			return errors.New("maf: the environment's host name, runtime version and operating system must not be blank")
		}
	}
	if environment.ApplicationVersion != nil {
		if ae.IsNullOrWhiteSpace(*environment.ApplicationVersion) {
			return errors.New("maf: the environment's application version must not be blank; leave it nil for none")
		}
		texts = append(texts, *environment.ApplicationVersion)
	}
	for key, value := range environment.Metadata {
		if ae.IsNullOrWhiteSpace(key) {
			return errors.New("maf: an environment metadata key must not be blank")
		}
		texts = append(texts, key, value)
	}
	for _, text := range texts {
		if !utf8.ValidString(text) {
			return errors.New("maf: the environment holds text that is not valid UTF-8")
		}
	}
	return nil
}

// resolveIdentity is the invocation's identity: the fixed one, or the resolver's, checked (a development identity is
// refused outside development). A panic in the resolver is returned as an error.
func (s *settings) resolveIdentity(ctx context.Context, identity IdentityContext) (resolved *ae.Identity, err error) {
	if s.ResolveIdentity == nil {
		fixed := s.Identity
		return &fixed, nil
	}
	defer recoverInto(&err)
	resolved, err = s.ResolveIdentity(ctx, identity)
	if err != nil || resolved == nil {
		return nil, err
	}
	if err := resolved.Validate(); err != nil {
		return nil, err
	}
	copied := *resolved
	return &copied, nil
}

func (s *settings) taskIDFor(a *agent.Agent) string {
	if s.TaskID != "" {
		return s.TaskID
	}
	if reduced, ok := ae.ReduceName(a.Name()); ok {
		return reduced
	}
	return DefaultTaskID
}

func (s *settings) reportInjection(failure InjectionFailure) {
	if s.OnInjectionFailure == nil {
		return
	}
	defer func() { _ = recover() }()
	s.OnInjectionFailure(failure)
}

// UseAgentExperience wires capture and injection into an agent's configuration, before the agent is built from it
// (agent.New, or a provider's constructor, which takes an agent.Config).
//
// It puts the capture middleware first in config.Middlewares and the tool middleware first in
// config.FunctionMiddlewares, so both wrap everything else, and appends the context provider to
// config.ContextProviders, unless the configuration already holds a *ContextProvider (any one, deliberately by type:
// the agent then gets exactly one block) or DisableInjection is set. Everything the configuration already holds is
// kept. A second call on the same configuration is ErrAlreadyWired; invalid options are an error, and so is a
// development identity outside a development or test environment.
func UseAgentExperience(config *agent.Config, options Options) error {
	if config == nil {
		return errors.New("maf: the agent configuration is nil")
	}
	for _, middleware := range config.Middlewares {
		if _, ok := middleware.(*captureMiddleware); ok {
			return ErrAlreadyWired
		}
	}
	s, err := newSettings(options)
	if err != nil {
		return err
	}
	config.Middlewares = append([]agent.Middleware{&captureMiddleware{settings: s}}, config.Middlewares...)
	config.FunctionMiddlewares = append([]agent.FunctionInvocationMiddleware{s.toolMiddleware},
		config.FunctionMiddlewares...)
	if s.DisableInjection {
		return nil
	}
	for _, provider := range config.ContextProviders {
		if _, ok := provider.(*ContextProvider); ok {
			return nil
		}
	}
	config.ContextProviders = append(config.ContextProviders, &ContextProvider{settings: s})
	return nil
}

// NewContextProvider is the injection alone, to wire by hand (UseAgentExperience adds one). Without the capture
// middleware of the same UseAgentExperience call it resolves the identity itself, and records no exposure.
func NewContextProvider(options Options) (*ContextProvider, error) {
	s, err := newSettings(options)
	if err != nil {
		return nil, err
	}
	return &ContextProvider{settings: s}, nil
}

// ---- Error and panic names -------------------------------------------------------------------------------------

// errorName is how an error is recorded: its type's name, never its message (which may hold model text or
// secrets). The caller's own cancellation is recorded as such.
func errorName(err error) string {
	switch {
	case errors.Is(err, context.Canceled):
		return CanceledError
	case errors.Is(err, context.DeadlineExceeded):
		return DeadlineExceededError
	}
	// fmt.Errorf's and errors.Join's wrappers say nothing of their own: the error they wrap is named.
	for {
		switch typeName(err) {
		case "fmt.wrapError", "fmt.wrapErrors", "errors.joinError":
			var inner error
			switch wrapped := err.(type) {
			case interface{ Unwrap() error }:
				inner = wrapped.Unwrap()
			case interface{ Unwrap() []error }:
				if errs := wrapped.Unwrap(); len(errs) > 0 {
					inner = errs[0]
				}
			}
			if inner == nil {
				return typeName(err)
			}
			err = inner
			continue
		}
		return typeName(err)
	}
}

// panicName is how a panic is recorded: the type of the value it carried, never the value.
func panicName(value any) string {
	if err, ok := value.(error); ok {
		return "panic(" + errorName(err) + ")"
	}
	return "panic(" + typeName(value) + ")"
}

// typeName is a value's type as package path and name ("io/fs.PathError"), pointers dereferenced.
func typeName(value any) string {
	t := reflect.TypeOf(value)
	if t == nil {
		return "nil"
	}
	for t.Kind() == reflect.Pointer {
		t = t.Elem()
	}
	switch {
	case t.Name() == "":
		return t.String()
	case t.PkgPath() == "":
		return t.Name()
	}
	return t.PkgPath() + "." + t.Name()
}

// panicError is a recovered panic as an error, for a report.
type panicError struct{ value any }

func (p panicError) Error() string { return "recovered " + panicName(p.value) }

// recoverInto turns a panic into *err.
func recoverInto(err *error) {
	if recovered := recover(); recovered != nil {
		*err = panicError{recovered}
	}
}
