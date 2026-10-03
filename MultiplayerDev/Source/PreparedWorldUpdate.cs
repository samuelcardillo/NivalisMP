using System;
using System.Diagnostics;

namespace NivalisTogether.Dedicated
{
    /// <summary>A detached managed result whose native commit belongs to the
    /// owning session's main thread and shared ordered preparation fence.</summary>
    internal interface IPreparedWorldUpdate
    {
        void Commit();
    }
    internal interface IIncrementalPreparedWorldUpdate : IPreparedWorldUpdate
    {
        bool Advance(ClientMutationBudget budget);
        void Abort();
    }

    /// <summary>Adapts the existing economy preparation without changing its
    /// public/profiling contract. It shares the same fence as other snapshots.</summary>
    internal sealed class PreparedWorldUpdate : IPreparedWorldUpdate
    {
        private Action commit;
        internal static bool Advance(IPreparedWorldUpdate update,ClientMutationBudget budget)
        {
            if(update==null||budget==null)throw new ArgumentNullException();
            if(update is IIncrementalPreparedWorldUpdate incremental)return incremental.Advance(budget);
            if(!budget.TryStart(Stopwatch.GetTimestamp()))return false;
            update.Commit();return true;
        }
        internal PreparedWorldUpdate(Action commit) => this.commit = commit ?? throw new ArgumentNullException(nameof(commit));
        public void Commit()
        {
            var action = commit ?? throw new InvalidOperationException("Prepared world update was already consumed.");
            commit = null; // A failed native commit must not be retried in-place.
            action();
        }
    }
}
