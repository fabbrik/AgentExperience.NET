using System.Security.Cryptography;

namespace AgentExperience.Abstractions.Tests;

/// <summary>
/// Story 16.3: the port's default <see cref="IExperienceKeyStore.GetKeysAsync"/>, which every key store that
/// does not override it answers through.
/// </summary>
public class KeyStoreBatchDefaultTests
{
    private static readonly Scope Scope = new("tenant-1", "app-1", "project-1");

    [Fact]
    public async Task The_default_asks_the_single_lookup_for_each_reference_in_order_and_answers_in_that_order()
    {
        var active = new ExperienceKeyReference(Guid.NewGuid(), Scope);
        var destroyed = new ExperienceKeyReference(Guid.NewGuid(), Scope);
        var missing = new ExperienceKeyReference(Guid.NewGuid(), Scope);
        var store = new ScriptedKeyStore { Destroyed = { destroyed.ExperienceId } };
        store.Known.Add(active.ExperienceId);
        IExperienceKeyStore port = store;

        var batch = await port.GetKeysAsync([active, destroyed, missing, active], CancellationToken.None);

        Assert.Equal([active, destroyed, missing, active], store.Lookups);
        Assert.Equal(
            [ExperienceKeyStatus.Active, ExperienceKeyStatus.Destroyed, ExperienceKeyStatus.NotFound, ExperienceKeyStatus.Active],
            batch.Select(lookup => lookup.Status));
        Assert.Same(store.Issued[0], batch[0].Key);
        Assert.Same(store.Issued[1], batch[3].Key);
        Assert.Empty(await port.GetKeysAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task When_a_lookup_throws_the_default_disposes_every_key_already_obtained_and_rethrows()
    {
        var failing = new ExperienceKeyReference(Guid.NewGuid(), Scope);
        var first = new ExperienceKeyReference(Guid.NewGuid(), Scope);
        var second = new ExperienceKeyReference(Guid.NewGuid(), Scope);
        var after = new ExperienceKeyReference(Guid.NewGuid(), Scope);
        var store = new ScriptedKeyStore { FailOn = failing.ExperienceId };
        store.Known.UnionWith([first.ExperienceId, second.ExperienceId, after.ExperienceId]);
        IExperienceKeyStore port = store;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => port.GetKeysAsync([first, second, failing, after], CancellationToken.None).AsTask());

        Assert.Equal([first, second, failing], store.Lookups);
        Assert.Equal(2, store.Issued.Count);
        Assert.All(store.Issued, key => Assert.Throws<ObjectDisposedException>(() => key.Span.Length));
    }

    [Fact]
    public async Task The_default_refuses_a_null_list()
    {
        IExperienceKeyStore port = new ScriptedKeyStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() => port.GetKeysAsync(null!, CancellationToken.None).AsTask());
    }

    /// <summary>A key store that implements only the single-key members, as every pre-16.3 store does.</summary>
    private sealed class ScriptedKeyStore : IExperienceKeyStore
    {
        public HashSet<Guid> Known { get; } = [];

        public HashSet<Guid> Destroyed { get; init; } = [];

        public Guid? FailOn { get; init; }

        public List<ExperienceKeyReference> Lookups { get; } = [];

        public List<ExperienceDataKey> Issued { get; } = [];

        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            Lookups.Add(reference);
            if (reference.ExperienceId == FailOn)
            {
                throw new InvalidOperationException("The key store is unreachable.");
            }

            if (Destroyed.Contains(reference.ExperienceId))
            {
                return ValueTask.FromResult(ExperienceKeyLookup.Destroyed);
            }

            if (!Known.Contains(reference.ExperienceId))
            {
                return ValueTask.FromResult(ExperienceKeyLookup.NotFound);
            }

            var key = new ExperienceDataKey(RandomNumberGenerator.GetBytes(ExperienceDataKey.SizeInBytes));
            Issued.Add(key);
            return ValueTask.FromResult(ExperienceKeyLookup.Active(key));
        }

        public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
