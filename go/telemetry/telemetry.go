// Package telemetry reports what AgentExperience does to OpenTelemetry: the spans and metrics that docs/telemetry.md
// documents for .NET, under the same source and meter names, span names, instruments, dimensions and attributes, for
// the operations the Go port performs.
//
// The core and the Agent Framework integration only call a small observer hook (agentexperience.SetObserver) and
// import no OpenTelemetry. This module is the optional adapter: it depends on the OpenTelemetry API only, configures
// no SDK and no exporter (the host does, as it does for the .NET library), and is enabled with one call:
//
//	disable := telemetry.Enable() // uses the global tracer and meter providers the application configured
//	defer disable()
//
// A failure in a provider, an exporter or this adapter is recovered and never reaches the agent. Attributes never
// carry record content, task text, scopes or error messages; a failing operation records its error's type name only.
package telemetry

import (
	"context"
	"runtime/debug"
	"sync"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// The names the adapter writes. Tests pin each against docs/telemetry-names.json, which is generated from the .NET
// definitions.
const (
	SpanNamePrefix    = "agentexperience."
	OperationCount    = "agentexperience.operation.count"
	OperationDuration = "agentexperience.operation.duration"
	OperationFailures = "agentexperience.operation.failures"

	// The metric dimensions.
	DimensionOperation  = "operation"
	DimensionOutcome    = "outcome"
	DimensionErrorClass = "error.class"
	DimensionNested     = "nested"

	// The span attributes the adapter writes itself.
	AttributeOperation  = "agentexperience.operation"
	AttributeOutcome    = "agentexperience.outcome"
	AttributeErrorClass = "agentexperience.error.class"
	AttributeErrorType  = "error.type"
)

// Instruments are the three instruments every meter publishes: name, kind and unit.
func Instruments() [][3]string {
	return [][3]string{
		{OperationCount, "counter", "{operation}"},
		{OperationDuration, "histogram", "s"},
		{OperationFailures, "counter", "{failure}"},
	}
}

// Dimensions are the only metric dimensions.
func Dimensions() []string {
	return []string{DimensionOperation, DimensionOutcome, DimensionErrorClass, DimensionNested}
}

// SpanAttributes is every span attribute the adapter can write.
func SpanAttributes() []string {
	return append([]string{AttributeOperation, AttributeOutcome, AttributeErrorClass, AttributeErrorType},
		ae.ObservedAttributes()...)
}

// Option configures an Observer.
type Option func(*Observer)

// WithTracerProvider reports spans to tp instead of the global tracer provider.
func WithTracerProvider(tp trace.TracerProvider) Option { return func(o *Observer) { o.tracers = tp } }

// WithMeterProvider reports metrics to mp instead of the global meter provider.
func WithMeterProvider(mp metric.MeterProvider) Option { return func(o *Observer) { o.meters = mp } }

// Observer is the agentexperience.Observer that reports to OpenTelemetry.
type Observer struct {
	tracers trace.TracerProvider
	meters  metric.MeterProvider

	mu          sync.Mutex
	instruments map[string]*instruments
}

var _ ae.Observer = (*Observer)(nil)

type instruments struct {
	tracer   trace.Tracer
	count    metric.Int64Counter
	duration metric.Float64Histogram
	failures metric.Int64Counter
}

// NewObserver is an observer reporting to the global providers, or to the ones the options name.
func NewObserver(options ...Option) *Observer {
	o := &Observer{instruments: map[string]*instruments{}}
	for _, option := range options {
		option(o)
	}
	return o
}

// Enable registers an observer built from options with the library and returns the function that unregisters it.
func Enable(options ...Option) (disable func()) {
	observer := NewObserver(options...)
	ae.SetObserver(observer)
	return func() {
		if ae.CurrentObserver() == ae.Observer(observer) {
			ae.SetObserver(nil)
		}
	}
}

func (o *Observer) forSource(source string) (*instruments, error) {
	o.mu.Lock()
	defer o.mu.Unlock()
	if found := o.instruments[source]; found != nil {
		return found, nil
	}
	tracers, meters := o.tracers, o.meters
	if tracers == nil {
		tracers = otel.GetTracerProvider()
	}
	if meters == nil {
		meters = otel.GetMeterProvider()
	}
	version := moduleVersion()
	meter := meters.Meter(source, metric.WithInstrumentationVersion(version))
	made := &instruments{tracer: tracers.Tracer(source, trace.WithInstrumentationVersion(version))}
	var err error
	if made.count, err = meter.Int64Counter(OperationCount, metric.WithUnit("{operation}"),
		metric.WithDescription("Instrumented Experience operations, by operation and outcome.")); err != nil {
		return nil, err
	}
	if made.duration, err = meter.Float64Histogram(OperationDuration, metric.WithUnit("s"),
		metric.WithDescription("How long each instrumented Experience operation took.")); err != nil {
		return nil, err
	}
	if made.failures, err = meter.Int64Counter(OperationFailures, metric.WithUnit("{failure}"),
		metric.WithDescription("Instrumented Experience operations that threw.")); err != nil {
		return nil, err
	}
	o.instruments[source] = made
	return made, nil
}

func moduleVersion() string {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return ""
	}
	for _, dependency := range info.Deps {
		if dependency.Path == "github.com/fabbrik/AgentExperience.NET/go" && dependency.Version != "(devel)" {
			return dependency.Version
		}
	}
	return ""
}

var allowedAttributes = func() map[string]bool {
	allowed := map[string]bool{}
	for _, name := range ae.ObservedAttributes() {
		allowed[name] = true
	}
	return allowed
}()

type token struct {
	span    trace.Span
	started time.Time
}

// Start opens the operation's span (when it has one) as a child of the span ctx carries, and returns a context
// carrying it, so the operations it runs are its children.
func (o *Observer) Start(ctx context.Context, source, operation string, _ bool) (context.Context, any) {
	started := time.Now()
	spec, known := ae.ObservedOperation(operation)
	if !known || !spec.Span {
		return ctx, &token{started: started}
	}
	found, err := o.forSource(source)
	if err != nil {
		return ctx, nil
	}
	ctx, span := found.tracer.Start(ctx, SpanNamePrefix+operation, trace.WithSpanKind(trace.SpanKindInternal),
		trace.WithAttributes(attribute.String(AttributeOperation, operation)))
	return ctx, &token{span: span, started: started}
}

// Finish records the operation's measurements and closes its span.
func (o *Observer) Finish(value any, observation ae.Observation) {
	state, ok := value.(*token)
	if !ok || state == nil {
		return
	}
	elapsed := time.Since(state.started).Seconds()
	if state.span != nil {
		defer state.span.End()
	}
	found, err := o.forSource(observation.Source)
	if err == nil {
		dimensions := metric.WithAttributes(
			attribute.String(DimensionOperation, observation.Operation),
			attribute.String(DimensionOutcome, observation.Outcome),
			attribute.Bool(DimensionNested, observation.Nested))
		found.count.Add(context.Background(), 1, dimensions)
		found.duration.Record(context.Background(), elapsed, dimensions)
		if observation.ErrorClass != "" {
			found.failures.Add(context.Background(), 1, metric.WithAttributes(
				attribute.String(DimensionOperation, observation.Operation),
				attribute.String(DimensionErrorClass, observation.ErrorClass),
				attribute.Bool(DimensionNested, observation.Nested)))
		}
	}
	if state.span == nil {
		return
	}
	attributes := []attribute.KeyValue{attribute.String(AttributeOutcome, observation.Outcome)}
	for _, item := range observation.Attributes {
		if !allowedAttributes[item.Key] {
			continue
		}
		switch value := item.Value.(type) {
		case string:
			attributes = append(attributes, attribute.String(item.Key, value))
		case int:
			attributes = append(attributes, attribute.Int(item.Key, value))
		case bool:
			attributes = append(attributes, attribute.Bool(item.Key, value))
		}
	}
	if observation.ErrorClass != "" {
		attributes = append(attributes, attribute.String(AttributeErrorClass, observation.ErrorClass))
	}
	if observation.ErrorType != "" {
		attributes = append(attributes, attribute.String(AttributeErrorType, observation.ErrorType))
	}
	state.span.SetAttributes(attributes...)
	if observation.Outcome == ae.OutcomeFaulted {
		state.span.SetStatus(codes.Error, "")
	} else {
		state.span.SetStatus(codes.Ok, "")
	}
}
