package maf

// Contract 1.14's reflection families through this port: each case's run is captured and finalized with a
// ChatClientReflector over a scripted chat client answering reply.json, as the .NET fixtures were generated. The
// reflection-output cases' stored records and events are the .NET finalization's, byte for byte as values: the Go half
// of the cross-language finalization with the model-backed reflector.

import (
	"context"
	"encoding/json"
	"errors"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/message"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// scriptedClient answers one call as reply.json says and records what it was sent.
type scriptedClient struct {
	reply    *ae.Object
	messages []*message.Message
	options  *ChatOptions
	calls    int
}

func member(o *ae.Object, key string) any {
	value, _ := o.Get(key)
	return value
}

func (c *scriptedClient) GetResponse(ctx context.Context, messages []*message.Message, options *ChatOptions) (*ChatResponse, error) {
	c.calls++
	c.messages, c.options = messages, options
	if delay, _ := member(c.reply, "delayMilliseconds").(json.Number); delay != "0" && delay != "" {
		milliseconds, _ := delay.Int64()
		select {
		case <-time.After(time.Duration(milliseconds) * time.Millisecond):
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	if throws, _ := member(c.reply, "throws").(bool); throws {
		return nil, errors.New("the provider answered 500 with a body quoting the prompt")
	}
	response := &ChatResponse{}
	if model, ok := member(c.reply, "modelId").(string); ok {
		response.ModelID = model
	}
	for _, item := range member(c.reply, "messages").([]any) {
		m := item.(*ae.Object)
		built := &message.Message{Role: message.Role(member(m, "role").(string))}
		for _, contentItem := range member(m, "contents").([]any) {
			content := contentItem.(*ae.Object)
			text, _ := member(content, "text").(string)
			callID, _ := member(content, "callId").(string)
			name, _ := member(content, "name").(string)
			switch member(content, "type") {
			case "text":
				built.Contents = append(built.Contents, &message.TextContent{Text: text})
			case "reasoning":
				built.Contents = append(built.Contents, &message.TextReasoningContent{Text: text})
			case "functionCall":
				built.Contents = append(built.Contents, &message.FunctionCallContent{CallID: callID, Name: name})
			case "functionResult":
				built.Contents = append(built.Contents, &message.FunctionResultContent{CallID: callID, Result: text})
			}
		}
		response.Messages = append(response.Messages, built)
	}
	return response, nil
}

// capturing is the reflector, recording the request it was asked about (it declares the same run content).
type capturing struct {
	*ChatClientReflector
	request *ae.ReflectionRequest
}

func (c *capturing) Reflect(ctx context.Context, request ae.ReflectionRequest) (ae.Reflection, error) {
	c.request = &request
	return c.ChatClientReflector.Reflect(ctx, request)
}

type reflectionCase struct {
	input, reply, reflector *ae.Object
	client                  *scriptedClient
}

func loadReflectionCase(t *testing.T, family, name string) reflectionCase {
	t.Helper()
	dir := filepath.Join(ct.Fixtures, family, name)
	c := reflectionCase{input: ct.MustReadJSON(filepath.Join(dir, "input.json")).(*ae.Object),
		reply:     ct.MustReadJSON(filepath.Join(dir, "reply.json")).(*ae.Object),
		reflector: ct.MustReadJSON(filepath.Join(dir, "reflector.json")).(*ae.Object)}
	c.client = &scriptedClient{reply: c.reply}
	return c
}

func (c reflectionCase) options() ChatClientReflectorOptions {
	number := func(key string) float64 { return ct.Number(member(c.reflector, key)) }
	options := ChatClientReflectorOptions{
		Timeout:         time.Duration(number("timeoutSeconds") * float64(time.Second)),
		MaxQuotedLength: int(number("maxQuotedLength")), MaxPromptLength: int(number("maxPromptLength")),
		MaxOutputTokens: int(number("maxOutputTokens")),
	}
	if model, ok := member(c.reflector, "modelName").(string); ok {
		options.ModelName = model
	}
	if temperature := member(c.reflector, "temperature"); temperature != nil {
		t := float32(ct.Number(temperature))
		options.Temperature = &t
	} else {
		options.NoTemperature = true
	}
	return options
}

func readJSON(t *testing.T, family, name, file string) any {
	t.Helper()
	return ct.MustReadJSON(filepath.Join(ct.Fixtures, family, name, file))
}

func assertSameJSON(t *testing.T, expected, actual any, what string) {
	t.Helper()
	if difference := ct.Difference(expected, ct.AsWritten(actual), "$", ct.Options{IgnoreKind: true}); difference != "" {
		t.Fatalf("%s: %s", what, difference)
	}
}

// optionsJSON is a call's options in expected-options.json's form.
func optionsJSON(t *testing.T, options *ChatOptions) *ae.Object {
	var model, temperature, tools any
	if options.ModelID != "" {
		model = options.ModelID
	}
	if options.Temperature != nil {
		temperature = json.Number(strconv.FormatFloat(float64(*options.Temperature), 'g', -1, 32))
	}
	if options.Tools != nil {
		names := []any{}
		for _, item := range options.Tools {
			names = append(names, item.Name())
		}
		tools = names
	}
	mode := any(nil)
	if options.ToolMode == tool.ToolModeNone {
		mode = "None"
	}
	schema, err := ae.DecodeJSON(options.ResponseFormat.Schema.(json.RawMessage))
	if err != nil {
		t.Fatal(err)
	}
	return ae.NewObject(
		ae.Member{Key: "modelId", Value: model},
		ae.Member{Key: "temperature", Value: temperature},
		ae.Member{Key: "maxOutputTokens", Value: json.Number(strconv.Itoa(options.MaxOutputTokens))},
		ae.Member{Key: "toolMode", Value: mode},
		ae.Member{Key: "tools", Value: tools},
		ae.Member{Key: "responseFormat", Value: ae.NewObject(
			ae.Member{Key: "schemaName", Value: options.ResponseFormat.Name},
			ae.Member{Key: "schemaDescription", Value: options.ResponseFormat.Description},
			ae.Member{Key: "schema", Value: schema})},
	)
}

func TestTheReflectionPromptFixturesAreWhatTheReflectorSends(t *testing.T) {
	const family = "reflection-prompt"
	cases := ct.Cases(family)
	if len(cases) != 6 {
		t.Fatalf("%s cases: %v", family, cases)
	}
	for _, name := range cases {
		t.Run(name, func(t *testing.T) {
			c := loadReflectionCase(t, family, name)
			reflector, err := NewChatClientReflector(c.client, c.options())
			if err != nil {
				t.Fatal(err)
			}
			wrapped := &capturing{ChatClientReflector: reflector}
			if _, _, err := ct.RunFinalizeCaseWith(c.input, wrapped); err != nil {
				t.Fatal(err)
			}
			messages := []any{}
			for _, m := range c.client.messages {
				messages = append(messages, ae.NewObject(ae.Member{Key: "role", Value: string(m.Role)},
					ae.Member{Key: "text", Value: m.String()}))
			}
			assertSameJSON(t, readJSON(t, family, name, "expected-messages.json"), messages, "expected-messages.json")
			assertSameJSON(t, readJSON(t, family, name, "expected-options.json"), optionsJSON(t, c.client.options),
				"expected-options.json")
			shown, err := reflector.ReflectedRunContent(*wrapped.request)
			if err != nil {
				t.Fatal(err)
			}
			list := []any{}
			for _, text := range shown {
				list = append(list, text)
			}
			assertSameJSON(t, readJSON(t, family, name, "expected-shown.json"), list, "expected-shown.json")
		})
	}
}

func TestTheReflectionOutputFixturesAreWhatFinalizationStores(t *testing.T) {
	const family = "reflection-output"
	cases := ct.Cases(family)
	if len(cases) != 24 {
		t.Fatalf("%s cases: %v", family, cases)
	}
	for _, name := range cases {
		t.Run(name, func(t *testing.T) {
			c := loadReflectionCase(t, family, name)
			reflector, err := NewChatClientReflector(c.client, c.options())
			if err != nil {
				t.Fatal(err)
			}
			finalized, _, err := ct.RunFinalizeCaseWith(c.input, reflector)
			if err != nil {
				t.Fatal(err)
			}
			assertSameJSON(t, readJSON(t, family, name, "expected-record.json"), finalized.Record.ToStored(), "expected-record.json")
			assertSameJSON(t, readJSON(t, family, name, "expected-event.json"), finalized.Event.ToJSON(), "expected-event.json")

			expected := readJSON(t, family, name, "expected.json").(*ae.Object)
			if outcome := member(expected, "outcome"); outcome != string(finalized.Record.Status) {
				t.Fatalf("outcome %s, not %v", finalized.Record.Status, outcome)
			}
			failure, _ := member(expected, "reflectionFailure").(*ae.Object)
			var failed *ReflectionFailedError
			switch {
			case failure == nil && finalized.ReflectionFailure != nil:
				t.Fatalf("an unexpected reflection failure: %v", finalized.ReflectionFailure)
			case failure != nil && !errors.As(finalized.ReflectionFailure, &failed):
				t.Fatalf("no reflection failure, but %v expected (got %v)", failure, finalized.ReflectionFailure)
			case failure != nil:
				cause, _ := member(failure, "causeType").(string)
				message := member(failure, "message").(string)
				if cause != "" {
					message = strings.Replace(message, cause, failed.CauseType, 1)
				}
				if string(failed.Kind) != member(failure, "kind") || (cause == "") != (failed.CauseType == "") ||
					failed.Error() != message {
					t.Fatalf("failure %s (%q): %q; expected %v", failed.Kind, failed.CauseType, failed.Error(), failure)
				}
			}
			screening, _ := member(expected, "screening").(*ae.Object)
			refusal, refused := ae.ScreeningRefusalOf(finalized.ScreeningRefusal)
			switch {
			case screening == nil && finalized.ScreeningRefusal != nil:
				t.Fatalf("an unexpected screening refusal: %v", finalized.ScreeningRefusal)
			case screening != nil && !refused:
				t.Fatalf("no screening refusal, but %v expected", screening)
			case screening != nil && (string(refusal.Code) != member(screening, "refusal") || refusal.Reason != member(screening, "reason")):
				t.Fatalf("refusal %s %q; expected %v", refusal.Code, refusal.Reason, screening)
			}
			if member(expected, "outcome") == "Validated" && finalized.Record.Reflection.Authorship != ae.AuthorshipModel {
				t.Fatal("the reflection is not model-authored")
			}
			if name == "prompt-too-large" && c.client.calls != 0 {
				t.Fatal("the model was called")
			}
		})
	}
}
