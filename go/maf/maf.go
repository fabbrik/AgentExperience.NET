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
	ExperienceIDs  []ae.UUID
	ByteCount      int
	Truncated      bool
	VectorFallback *ae.VectorFallback
	// WithheldExperienceIDs are the records a held-out invocation's block named and did not deliver (contract 1.8):
	// set only in what OnWithheld is told, where ExperienceIDs is empty and ByteCount 0.
	WithheldExperienceIDs []ae.UUID
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
	// PreferredEnvironment is the metadata ranking prefers.
	PreferredEnvironment map[string]string
	// ExcludeModelAuthored leaves model-authored lessons out of the search and the ranking.
	ExcludeModelAuthored bool
	// MessageRole is the injected message's role: message.RoleUser (when empty) or message.RoleSystem.
	MessageRole message.Role
	// DisableInjection captures without injecting.
	DisableInjection bool
	// OnCaptureFailure, OnInjectionFailure, OnInjected, OnFinalized and OnInjectionRecorded are told what happened;
	// each is optional, and a panic in one is recovered and ignored. OnInjectionRecorded is called from the
	// recording's own goroutine.
	OnCaptureFailure    func(CaptureFailure)
	OnInjectionFailure  func(InjectionFailure)
	OnInjected          func(InjectionResult)
	OnFinalized         func(ae.FinalizedExperience)
	OnInjectionRecorded func(InjectionRecordResult)
	// Holdout is the injection holdout (contract 1.8); off when zero.
	Holdout HoldoutOptions
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
	// never are.
	MinVectorRelevance *float64
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
	if math.IsNaN(options.Holdout.Rate) || options.Holdout.Rate < 0 || options.Holdout.Rate > 1 {
		return nil, errors.New("maf: the holdout rate must be a number from 0 to 1")
	}
	if options.Holdout.Salt == "" {
		options.Holdout.Salt = ae.DefaultHoldoutSalt
	}
	if options.Clock == nil {
		options.Clock = ae.Now
	}
	if options.NewID == nil {
		options.NewID = ae.NewRandomUUID
	}
	s := &settings{Options: options, vectorSlots: make(chan struct{}, MaxVectorCalls)}
	if options.Environment != nil {
		s.environment = *options.Environment
	} else {
		s.environment = DefaultEnvironment()
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
