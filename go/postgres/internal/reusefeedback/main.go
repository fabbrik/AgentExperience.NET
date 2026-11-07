// Command reusefeedback is the Go writer of the cross-language reuse-feedback test (contract 1.17): the .NET suite runs
// it as `go run ./internal/reusefeedback <input.json>` from go/postgres, beside a .NET and a Python writer, all
// recording reuse feedback on the same lessons through their own services over PostgreSQL.
//
// The input is {"dsn": "<postgres URL>", "hostTrustedEvidence": "Counted" | "RecordedOnly", "steps": [{"submit":
// {"clock", "authorization", "feedback"}} | {"read": {"authorization", "scope", "experienceId"}}]}. One lifecycle
// service (the default engine, the given effect) runs over the PostgreSQL store, and one ReuseFeedbackService over it
// and the store's ledger; the store's clock reads each submit step's clock. It prints {"results": [...]}, one per step:
// a submit step's result as the reuse-feedback fixtures write it, and a read step's {"history": <the record's history,
// limit 500, from the start>, "confidence": <its confidence read, filter All>}. Nothing else goes to stdout; a failure
// goes to stderr with exit status 1.
package main

import (
	"context"
	"errors"
	"fmt"
	"os"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
)

func main() {
	if len(os.Args) != 2 {
		fmt.Fprintln(os.Stderr, "usage: reusefeedback <input.json>")
		os.Exit(2)
	}
	output, err := run(context.Background(), os.Args[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, "reusefeedback:", err)
		os.Exit(1)
	}
	if _, err := os.Stdout.Write(append(output, '\n')); err != nil {
		os.Exit(1)
	}
}

func member(o *ae.Object, key string) any {
	value, _ := o.Get(key)
	return value
}

func run(ctx context.Context, path string) (output []byte, err error) {
	defer func() {
		// The fixture readers panic on a malformed input.
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("malformed input: %v", recovered)
		}
	}()
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	value, err := ae.DecodeJSON(data)
	if err != nil {
		return nil, err
	}
	input, ok := value.(*ae.Object)
	if !ok {
		return nil, errors.New("the input is not a JSON object")
	}
	dsn, _ := member(input, "dsn").(string)
	store, err := postgres.Open(ctx, dsn)
	if err != nil {
		return nil, err
	}
	defer store.Close()
	effect, _ := member(input, "hostTrustedEvidence").(string)
	lifecycle, err := ae.NewLifecycleService(store, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedEvidenceEffect(effect)})
	if err != nil {
		return nil, err
	}
	service, err := ae.NewReuseFeedbackService(store, lifecycle)
	if err != nil {
		return nil, err
	}

	results := []any{}
	steps, _ := member(input, "steps").([]any)
	for _, item := range steps {
		step, _ := item.(*ae.Object)
		if submit, ok := member(step, "submit").(*ae.Object); ok {
			clock := ae.MustParseInstant(member(submit, "clock").(string))
			store.SetClock(func() ae.Instant { return clock })
			result, err := service.Record(ctx, ct.AuthorizationOf(member(submit, "authorization")),
				ct.FeedbackOf(member(submit, "feedback")))
			if err != nil {
				results = append(results, ct.LedgerThrownJSON(err))
				continue
			}
			results = append(results, ct.SubmitResultJSON(result))
			continue
		}
		read, ok := member(step, "read").(*ae.Object)
		if !ok {
			return nil, fmt.Errorf("unknown step %v", step.Keys())
		}
		authorization := ct.AuthorizationOf(member(read, "authorization"))
		scope := ct.ScopeOf(member(read, "scope"))
		id, err := ae.ParseUUID(member(read, "experienceId").(string))
		if err != nil {
			return nil, err
		}
		history, err := store.GetHistory(ctx, authorization, ae.HistoryQuery{Scope: scope, ExperienceID: id,
			Limit: ae.HistoryMaxLimit})
		if err != nil {
			return nil, err
		}
		confidence, err := lifecycle.ReadConfidence(ctx, authorization, scope, id, ae.FilterAll)
		if err != nil {
			return nil, err
		}
		results = append(results, ae.NewObject(ae.Member{Key: "history", Value: ct.HistoryResultJSON(scope, history)},
			ae.Member{Key: "confidence", Value: ct.ConfidenceReadJSON(confidence)}))
	}
	return ae.MarshalJSONValue(ae.NewObject(ae.Member{Key: "results", Value: results}))
}
