﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Hangfire.InMemory.Entities;

namespace Hangfire.InMemory
{
    internal sealed class InMemoryState
    {
        private readonly BackgroundJobStateCreatedAtComparer _backgroundJobEntryComparer;

        internal readonly SortedSet<BackgroundJobEntry> _jobIndex;
        internal readonly SortedSet<CounterEntry> _counterIndex;
        internal readonly SortedSet<HashEntry> _hashIndex;
        internal readonly SortedSet<ListEntry> _listIndex;
        internal readonly SortedSet<SetEntry> _setIndex;

        // State index uses case-insensitive comparisons, despite of the current settings. SQL Server
        // uses case-insensitive by default, and Redis doesn't use state index that's based on user values.
        internal readonly IDictionary<string, SortedSet<BackgroundJobEntry>> _jobStateIndex = new Dictionary<string, SortedSet<BackgroundJobEntry>>(StringComparer.OrdinalIgnoreCase);

        internal readonly IDictionary<string, LockEntry> _locks;
        private readonly ConcurrentDictionary<string, BackgroundJobEntry> _jobs;
        private readonly Dictionary<string, HashEntry> _hashes;
        private readonly Dictionary<string, ListEntry> _lists;
        private readonly Dictionary<string, SetEntry> _sets;
        private readonly Dictionary<string, CounterEntry> _counters;
        private readonly ConcurrentDictionary<string, QueueEntry> _queues;
        private readonly Dictionary<string, ServerEntry> _servers;

        public InMemoryState(Func<DateTime> timeResolver, InMemoryStorageOptions options)
        {
            TimeResolver = timeResolver;
            Options = options;

            _backgroundJobEntryComparer = new BackgroundJobStateCreatedAtComparer(options.StringComparer);

            var expirableEntryComparer = new ExpirableEntryComparer(options.StringComparer);
            _jobIndex = new SortedSet<BackgroundJobEntry>(expirableEntryComparer);
            _counterIndex = new SortedSet<CounterEntry>(expirableEntryComparer);
            _hashIndex = new SortedSet<HashEntry>(expirableEntryComparer);
            _listIndex = new SortedSet<ListEntry>(expirableEntryComparer);
            _setIndex = new SortedSet<SetEntry>(expirableEntryComparer);

            _locks = CreateDictionary<LockEntry>(options.StringComparer);
            _jobs = CreateConcurrentDictionary<BackgroundJobEntry>(options.StringComparer);
            _hashes = CreateDictionary<HashEntry>(options.StringComparer);
            _lists = CreateDictionary<ListEntry>(options.StringComparer);
            _sets = CreateDictionary<SetEntry>(options.StringComparer);
            _counters = CreateDictionary<CounterEntry>(options.StringComparer);
            _queues = CreateConcurrentDictionary<QueueEntry>(options.StringComparer);
            _servers = CreateDictionary<ServerEntry>(options.StringComparer);
        }

        public Func<DateTime> TimeResolver { get; }
        public InMemoryStorageOptions Options { get; }

        public ConcurrentDictionary<string, BackgroundJobEntry> Jobs => _jobs; // TODO Implement workaround for net45 to return IReadOnlyDictionary (and the same for _queues)
        public IReadOnlyDictionary<string, HashEntry> Hashes => _hashes;
        public IReadOnlyDictionary<string, ListEntry> Lists => _lists;
        public IReadOnlyDictionary<string, SetEntry> Sets => _sets;
        public IReadOnlyDictionary<string, CounterEntry> Counters => _counters;
        public ConcurrentDictionary<string, QueueEntry> Queues => _queues; // net451 target does not have ConcurrentDictionary that implements IReadOnlyDictionary
        public IReadOnlyDictionary<string, ServerEntry> Servers => _servers;

        public QueueEntry QueueGetOrCreate(string name)
        {
            if (!_queues.TryGetValue(name, out var entry))
            {
                // TODO: Refactor this to unify creation of a queue
                entry = _queues.GetOrAdd(name, _ => new QueueEntry());
            }

            return entry;
        }

        public void JobCreate(BackgroundJobEntry job)
        {
            if (!_jobs.TryAdd(job.Key, job))
            {
                // TODO: Panic
            }

            _jobIndex.Add(job);
        }

        public void JobSetState(BackgroundJobEntry job, StateEntry state)
        {
            if (job.State != null && _jobStateIndex.TryGetValue(job.State.Name, out var indexEntry))
            {
                indexEntry.Remove(job);
                if (indexEntry.Count == 0) _jobStateIndex.Remove(job.State.Name);
            }

            job.State = state;

            if (!_jobStateIndex.TryGetValue(state.Name, out indexEntry))
            {
                _jobStateIndex.Add(state.Name, indexEntry = new SortedSet<BackgroundJobEntry>(_backgroundJobEntryComparer));
            }

            indexEntry.Add(job);
        }

        public void JobExpire(BackgroundJobEntry job, TimeSpan? expireIn)
        {
            EntryExpire(job, _jobIndex, expireIn);
        }

        public void JobDelete(BackgroundJobEntry entry)
        {
            if (entry.ExpireAt.HasValue)
            {
                _jobIndex.Remove(entry);
            }

            _jobs.TryRemove(entry.Key, out _);

            if (entry.State?.Name != null && _jobStateIndex.TryGetValue(entry.State.Name, out var stateIndex))
            {
                stateIndex.Remove(entry);
                if (stateIndex.Count == 0) _jobStateIndex.Remove(entry.State.Name);
            }
        }

        public HashEntry HashGetOrAdd(string key)
        {
            if (!_hashes.TryGetValue(key, out var hash))
            {
                _hashes.Add(key, hash = new HashEntry(key, Options.StringComparer));
            }

            return hash;
        }

        public void HashExpire(HashEntry hash, TimeSpan? expireIn)
        {
            EntryExpire(hash, _hashIndex, expireIn);
        }

        public void HashDelete(HashEntry hash)
        {
            _hashes.Remove(hash.Key);
            if (hash.ExpireAt.HasValue)
            {
                _hashIndex.Remove(hash);
            }
        }

        public SetEntry SetGetOrAdd(string key)
        {
            if (!_sets.TryGetValue(key, out var set))
            {
                _sets.Add(key, set = new SetEntry(key, Options.StringComparer));
            }

            return set;
        }

        public void SetExpire(SetEntry set, TimeSpan? expireIn)
        {
            EntryExpire(set, _setIndex, expireIn);
        }

        public void SetDelete(SetEntry set)
        {
            _sets.Remove(set.Key);

            if (set.ExpireAt.HasValue)
            {
                // TODO: Ensure entity removal always deals with expiration indexes
                _setIndex.Remove(set);
            }
        }

        public ListEntry ListGetOrAdd(string key)
        {
            if (!_lists.TryGetValue(key, out var list))
            {
                _lists.Add(key, list = new ListEntry(key, Options.StringComparer));
            }

            return list;
        }

        public void ListExpire(ListEntry entry, TimeSpan? expireIn)
        {
            EntryExpire(entry, _listIndex, expireIn);
        }

        public void ListDelete(ListEntry list)
        {
            _lists.Remove(list.Key);

            if (list.ExpireAt.HasValue)
            {
                _listIndex.Remove(list);
            }
        }

        public CounterEntry CounterGetOrAdd(string key)
        {
            if (!_counters.TryGetValue(key, out var counter))
            {
                _counters.Add(key, counter = new CounterEntry(key));
            }

            return counter;
        }

        public void CounterExpire(CounterEntry counter, TimeSpan? expireIn)
        {
            EntryExpire(counter, _counterIndex, expireIn);
        }

        public void CounterDelete(CounterEntry entry)
        {
            _counters.Remove(entry.Key);

            if (entry.ExpireAt.HasValue)
            {
                _counterIndex.Remove(entry);
            }
        }

        public void ServerAdd(string serverId, ServerEntry entry)
        {
            _servers.Add(serverId, entry);
        }

        public void ServerRemove(string serverId)
        {
            _servers.Remove(serverId);
        }

        private void EntryExpire<T>(T entity, ISet<T> index, TimeSpan? expireIn)
            where T : IExpirableEntry
        {
            if (entity.ExpireAt.HasValue)
            {
                index.Remove(entity);
            }

            if (expireIn.HasValue)
            {
                entity.ExpireAt = TimeResolver().Add(expireIn.Value);
                index.Add(entity);
            }
            else
            {
                entity.ExpireAt = null;
            }
        }

        private static Dictionary<string, T> CreateDictionary<T>(StringComparer comparer)
        {
            return new Dictionary<string, T>(comparer);
        }

        private static ConcurrentDictionary<string, T> CreateConcurrentDictionary<T>(StringComparer comparer)
        {
            return new ConcurrentDictionary<string, T>(comparer);
        }
    }
}