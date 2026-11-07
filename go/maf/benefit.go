package maf

import (
	"context"
	"errors"
	"sync"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// Acting on the benefit estimate (contract 1.10): the cache of estimates the benefit policy acts on, .NET's
// ExperienceBenefitCache. An invocation reads what is cached for its key as it is (nothing yet means no effect) and,
// when the entry is missing or older than the policy's RefreshInterval and no refresh is running for it, starts one in
// a goroutine and does not wait for it. A refresh that fails or overruns RefreshTimeout keeps the previous view; either
// way the entry counts as refreshed then, so a failing store is asked again only after another interval. Each refresh
// is reported as the benefit_refresh operation.

// MaxBenefitCacheEntries is the most keys the cache keeps: past it the entry refreshed longest ago that is not
// refreshing is dropped (.NET's ExperienceBenefitCache.MaxEntries).
const MaxBenefitCacheEntries = 10_000

// The benefit_refresh outcomes.
const (
	BenefitRefreshed      = "Refreshed"
	BenefitNotRefreshed   = "NotRefreshed"
	BenefitOutcomeUnknown = "OutcomeUnknown"
)

// benefitKey is a cache key: the request scope, the principal and the bounds it reads under. Two principals never
// share an entry.
type benefitKey struct {
	scope     [6]string
	principal string
	bounds    [6]string
}

func optionalKey(value *string) string {
	if value == nil {
		return "\x00"
	}
	return "=" + *value
}

func keyOf(identity *ae.Identity) benefitKey {
	s, b := identity.Scope, identity.Authorization()
	return benefitKey{
		scope: [6]string{s.TenantID, s.ApplicationID, s.ProjectID, optionalKey(s.TeamID), optionalKey(s.AgentID),
			optionalKey(s.UserID)},
		principal: identity.Principal,
		bounds: [6]string{b.TenantID, optionalKey(b.ApplicationID), optionalKey(b.ProjectID), optionalKey(b.TeamID),
			optionalKey(b.AgentID), optionalKey(b.UserID)},
	}
}

type benefitEntry struct {
	views       *ae.BenefitViews
	refreshed   bool
	refreshedAt ae.Instant
	refreshing  bool
}

// benefitCache is one setup's cached estimates. Safe for concurrent use.
type benefitCache struct {
	store  ae.Store
	policy ae.BenefitPolicy
	clock  func() ae.Instant

	mu       sync.Mutex
	entries  map[benefitKey]*benefitEntry
	refreshs tracker
}

func newBenefitCache(store ae.Store, policy ae.BenefitPolicy, clock func() ae.Instant) *benefitCache {
	return &benefitCache{store: store, policy: policy.WithDefaults(), clock: clock,
		entries: map[benefitKey]*benefitEntry{}}
}

// current is the view the cache holds for identity, for model (with PerModel; the pooled one otherwise), or nil; it
// starts a background refresh first when the entry is missing or stale and none is running for it. It never blocks
// on the store and never panics.
func (c *benefitCache) current(ctx context.Context, identity *ae.Identity, model *string) (view *ae.BenefitView) {
	defer func() {
		if recover() != nil {
			// A cache that cannot answer has no effect: the invocation is injected as without the policy.
			view = nil
		}
	}()
	key := keyOf(identity)
	now := c.clock()
	c.mu.Lock()
	defer c.mu.Unlock()
	entry, ok := c.entries[key]
	if !ok {
		if len(c.entries) >= MaxBenefitCacheEntries {
			c.evict()
		}
		entry = &benefitEntry{}
		c.entries[key] = entry
	}
	interval := int64(c.policy.RefreshInterval / 100)
	if !entry.refreshing && (!entry.refreshed || now.Ticks()-entry.refreshedAt.Ticks() >= interval) {
		entry.refreshing = true
		scoped := *identity
		c.refreshs.begin()
		go func() {
			defer c.refreshs.end()
			c.refresh(context.WithoutCancel(ctx), entry, &scoped)
		}()
	}
	if !c.policy.PerModel {
		model = nil
	}
	return entry.views.For(model)
}

// evict drops the entry refreshed longest ago (one never refreshed counts as oldest), unless it is refreshing. Called
// with mu held.
func (c *benefitCache) evict() {
	var oldestKey benefitKey
	var oldest *benefitEntry
	ticks := func(e *benefitEntry) int64 {
		if !e.refreshed {
			return -1
		}
		return e.refreshedAt.Ticks()
	}
	for key, entry := range c.entries {
		if entry.refreshing {
			continue
		}
		if oldest == nil || ticks(entry) < ticks(oldest) {
			oldestKey, oldest = key, entry
		}
	}
	if oldest != nil {
		delete(c.entries, oldestKey)
	}
}

// refresh reads the window's estimate within RefreshTimeout and replaces the entry's views when the store answered;
// otherwise keeps them. Reported as benefit_refresh. Never panics.
func (c *benefitCache) refresh(ctx context.Context, entry *benefitEntry, identity *ae.Identity) {
	_, op := ae.Observe(ctx, "benefit_refresh", false)
	outcome, class := BenefitRefreshed, ""
	var views *ae.BenefitViews
	replaced := false
	defer func() {
		if recover() != nil {
			outcome, class, replaced = BenefitNotRefreshed, ae.ErrorClassUnexpected, false
		}
		c.mu.Lock()
		if replaced {
			entry.views = views
		}
		// A failed refresh waits out the interval too, so a failing store is not asked on every invocation.
		entry.refreshed, entry.refreshedAt, entry.refreshing = true, c.clock(), false
		c.mu.Unlock()
		op.DoneWithClass(outcome, class)
	}()
	bounded, cancel := context.WithTimeout(ctx, c.policy.RefreshTimeout)
	defer cancel()
	type answer struct {
		estimate ae.BenefitEstimate
		err      error
	}
	done := make(chan answer, 1)
	from := c.policy.AssignedFrom(c.clock())
	go func() {
		// Its own goroutine, so a store that ignores its context cannot hold the timeout off; it is abandoned then.
		defer func() {
			if recovered := recover(); recovered != nil {
				done <- answer{err: panicError{recovered}}
			}
		}()
		estimate, err := ae.EstimateBenefitAssignedFrom(bounded, c.store, identity.Authorization(), identity.Scope,
			c.policy.EstimatorOptions(), from)
		done <- answer{estimate, err}
	}()
	select {
	case got := <-done:
		switch {
		case got.err == nil:
			views, replaced = ae.NewBenefitViews(got.estimate), true
		case bounded.Err() != nil:
			outcome, class = BenefitOutcomeUnknown, refreshTimeoutClass(bounded.Err())
		default:
			outcome, class = refreshFailure(got.err)
		}
	case <-bounded.Done():
		outcome, class = BenefitOutcomeUnknown, refreshTimeoutClass(bounded.Err())
	}
}

func refreshTimeoutClass(err error) string {
	if errors.Is(err, context.Canceled) {
		return ae.ErrorClassCancelled
	}
	return ae.ErrorClassTimeout
}

// refreshFailure is a failed refresh's outcome and error.class, as .NET's: a store that timed out (a deadline or a
// network timeout, even with the refresh's own bound not yet reached) or was cancelled is OutcomeUnknown with Timeout
// or Cancelled; any other store failure (a database driver's, a network's or a file system's) is NotRefreshed with
// Infrastructure, and anything else (a panic, a store that paged a run apart) NotRefreshed with Unexpected.
func refreshFailure(err error) (string, string) {
	if _, panicked := err.(panicError); panicked {
		return BenefitNotRefreshed, ae.ErrorClassUnexpected
	}
	switch class := ae.ClassifyError(err); class {
	case ae.ErrorClassTimeout, ae.ErrorClassCancelled:
		return BenefitOutcomeUnknown, class
	case ae.ErrorClassInfrastructure:
		return BenefitNotRefreshed, class
	}
	return BenefitNotRefreshed, ae.ErrorClassUnexpected
}

// size is how many keys are cached. For tests.
func (c *benefitCache) size() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.entries)
}

// WaitBenefitRefreshes waits for every benefit refresh the provider started (contract 1.10) to end, or for ctx to be
// done, whichever is first, and returns ctx's error in the second case. Each refresh is bounded by the policy's
// RefreshTimeout. Close awaits them too.
func (p *ContextProvider) WaitBenefitRefreshes(ctx context.Context) error {
	if p.settings.benefit == nil {
		return nil
	}
	return p.settings.benefit.refreshs.wait(ctx)
}

// invokingModel is the model the policy acts for with PerModel: the captured run's recorded model when the invocation
// is captured, otherwise the model the agent's chat client runs on (Options.Model, normalized); nil for none.
func (s *settings) invokingModel(captured bool) *string {
	if captured {
		if model, ok := s.environment.Metadata[ae.ModelMetadataKey]; ok {
			return &model
		}
		return nil
	}
	if model := ae.NormalizeModel(s.Model); model != "" {
		return &model
	}
	return nil
}
