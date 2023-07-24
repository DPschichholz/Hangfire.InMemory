using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Hangfire.Common;
using Hangfire.Storage;

namespace Hangfire.InMemory.Entities
{
    internal sealed class BackgroundJobEntry : IExpirableEntry
    {
        private const int StateCountForRegularJob = 4; // (Scheduled) -> Enqueued -> Processing -> Succeeded

        public BackgroundJobEntry(
            string key,
            Job job,
            IDictionary<string, string> parameters,
            DateTime createdAt,
            DateTime? expireAt,
            bool disableSerialization,
            StringComparer comparer)
        {
            Key = key;
            InvocationData = disableSerialization == false ? InvocationData.SerializeJob(job) : null;
            Job = disableSerialization ? new Job(job.Type, job.Method, job.Args.ToArray(), job.Queue) : null;
            Parameters = new ConcurrentDictionary<string, string>(parameters, comparer);
            CreatedAt = createdAt;
            ExpireAt = expireAt;
        }

        public string Key { get; }
        public InvocationData InvocationData { get; internal set; }
        public Job Job { get; }

        public ConcurrentDictionary<string, string> Parameters { get; }

        public StateEntry State { get; set; }
        public ICollection<StateEntry> History { get; } = new List<StateEntry>(StateCountForRegularJob);
        public DateTime CreatedAt { get; }
        public DateTime? ExpireAt { get; set; }

        public Job TryGetJob(out JobLoadException exception)
        {
            exception = null;

            if (Job != null)
            {
                return new Job(Job.Type, Job.Method, Job.Args.ToArray(), Job.Queue);
            }

            try
            {
                return InvocationData.DeserializeJob();
            }
            catch (JobLoadException ex)
            {
                exception = ex;
                return null;
            }
        }
    }
}