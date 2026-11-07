// Command lifecyclerace is the Go writer of the cross-language lifecycle race (contract 1.16): the .NET suite runs it
// as `go run ./internal/lifecyclerace <input.json>` from go/postgres, beside a .NET and a Python writer, all deciding
// transitions against the same revisions of the same records in one PostgreSQL database.
//
// The input is {"dsn": "<postgres URL>", "mode": "commit" | "history", "rounds": [{"lockKey": <int64>,
// "authorization": {...}, "request": <a lifecycle-commit fixture's commit request>}], "histories": [{"authorization":
// {...}, "query": {"scope", "experienceId", "limit", "startAfterRevision"}}]}.
//
// In commit mode, for each round in order, a connection of its own takes the shared advisory lock lockKey (which
// waits until the orchestrator releases its exclusive one) and releases it, then the round's request is committed
// through the lifecycle service over the PostgreSQL store. It prints {"results": [{"outcome", "revision",
// "currentStatus", "reason", "errors"}, ...]}. In history mode it reads each history page through the store and prints
// {"histories": [<a history result as the lifecycle-commit fixtures write it>, ...]}. Nothing else goes to stdout; a
// failure goes to stderr with exit status 1.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strconv"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
)

func main() {
	if len(os.Args) != 2 {
		fmt.Fprintln(os.Stderr, "usage: lifecyclerace <input.json>")
		os.Exit(2)
	}
	output, err := run(context.Background(), os.Args[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, "lifecyclerace:", err)
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

func list(o *ae.Object, key string) []any {
	items, _ := member(o, key).([]any)
	return items
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

	switch mode, _ := member(input, "mode").(string); mode {
	case "commit":
		results, err := commit(ctx, dsn, store, list(input, "rounds"))
		if err != nil {
			return nil, err
		}
		return ae.MarshalJSONValue(ae.NewObject(ae.Member{Key: "results", Value: results}))
	case "history":
		histories := []any{}
		for _, item := range list(input, "histories") {
			history, _ := item.(*ae.Object)
			query := ct.HistoryQueryOf(member(history, "query"))
			result, err := store.GetHistory(ctx, ct.AuthorizationOf(member(history, "authorization")), query)
			if err != nil {
				return nil, err
			}
			histories = append(histories, ct.HistoryResultJSON(query.Scope, result))
		}
		return ae.MarshalJSONValue(ae.NewObject(ae.Member{Key: "histories", Value: histories}))
	default:
		return nil, fmt.Errorf("unknown mode %q", mode)
	}
}

func commit(ctx context.Context, dsn string, store *postgres.Store, rounds []any) ([]any, error) {
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	if err != nil {
		return nil, err
	}
	locks, err := pgx.Connect(ctx, dsn)
	if err != nil {
		return nil, err
	}
	defer locks.Close(context.Background())
	results := []any{}
	for _, item := range rounds {
		round, _ := item.(*ae.Object)
		key, err := lockKey(member(round, "lockKey"))
		if err != nil {
			return nil, err
		}
		// Blocks until the orchestrator releases its exclusive lock: every writer then starts the round at once.
		if _, err := locks.Exec(ctx, "SELECT pg_advisory_lock_shared($1)", key); err != nil {
			return nil, err
		}
		if _, err := locks.Exec(ctx, "SELECT pg_advisory_unlock_shared($1)", key); err != nil {
			return nil, err
		}
		result, err := service.Commit(ctx, ct.AuthorizationOf(member(round, "authorization")),
			ct.CommitRequestOf(member(round, "request")))
		if err != nil {
			return nil, err
		}
		var currentStatus, reason any
		if result.CurrentStatus != nil {
			currentStatus = string(*result.CurrentStatus)
		}
		if result.Reason != nil {
			reason = *result.Reason
		}
		results = append(results, ae.NewObject(
			ae.Member{Key: "outcome", Value: string(result.Outcome)},
			ae.Member{Key: "revision", Value: json.Number(strconv.FormatInt(result.Revision, 10))},
			ae.Member{Key: "currentStatus", Value: currentStatus},
			ae.Member{Key: "reason", Value: reason},
			ae.Member{Key: "errors", Value: ct.ErrorsJSON(result.Errors)},
		))
	}
	return results, nil
}

func lockKey(value any) (int64, error) {
	number, ok := value.(interface{ Int64() (int64, error) })
	if !ok {
		return 0, errors.New("a round's lockKey must be an integer")
	}
	return number.Int64()
}
