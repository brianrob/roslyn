﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Options;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;

using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using VSCompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.AsyncCompletion
{
    internal sealed partial class ItemManager : IAsyncCompletionItemManager2
    {
        /// <summary>
        /// The threshold for us to consider exclude (potentially large amount of) expanded items from completion list.
        /// Showing a large amount of expanded items to user would introduce noise and render the list too long to browse.
        /// Not processing those expanded items also has perf benefit (e.g. matching and highlighting could be expensive.)
        /// We set it to 2 because it's common to use filter of length 2 for camel case match, e.g. `AB` for `ArrayBuiler`.
        /// </summary>
        internal const int FilterTextLengthToExcludeExpandedItemsExclusive = 2;

        private readonly RecentItemsManager _recentItemsManager;
        private readonly EditorOptionsService _editorOptionsService;

        internal ItemManager(RecentItemsManager recentItemsManager, EditorOptionsService editorOptionsService)
        {
            _recentItemsManager = recentItemsManager;
            _editorOptionsService = editorOptionsService;
        }

        public Task<ImmutableArray<VSCompletionItem>> SortCompletionListAsync(
            IAsyncCompletionSession session,
            AsyncCompletionSessionInitialDataSnapshot data,
            CancellationToken cancellationToken)
        {
            var stopwatch = SharedStopwatch.StartNew();
            var items = SortCompletionItems(data, cancellationToken).ToImmutableArray();

            AsyncCompletionLogger.LogItemManagerSortTicksDataPoint(stopwatch.Elapsed);
            return Task.FromResult(items);
        }

        public Task<CompletionList<VSCompletionItem>> SortCompletionItemListAsync(
            IAsyncCompletionSession session,
            AsyncCompletionSessionInitialDataSnapshot data,
            CancellationToken cancellationToken)
        {
            var stopwatch = SharedStopwatch.StartNew();
            var itemList = session.CreateCompletionList(SortCompletionItems(data, cancellationToken));

            AsyncCompletionLogger.LogItemManagerSortTicksDataPoint(stopwatch.Elapsed);
            return Task.FromResult(itemList);
        }

        private static SegmentedList<VSCompletionItem> SortCompletionItems(AsyncCompletionSessionInitialDataSnapshot data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = new SegmentedList<VSCompletionItem>(data.InitialItemList.Count);
            foreach (var item in data.InitialItemList)
            {
                CompletionItemData.GetOrAddDummyRoslynItem(item);
                items.Add(item);
            }

            items.Sort(VSItemComparer.Instance);
            return items;
        }

        public async Task<FilteredCompletionModel?> UpdateCompletionListAsync(
            IAsyncCompletionSession session,
            AsyncCompletionSessionDataSnapshot data,
            CancellationToken cancellationToken)
        {
            var stopwatch = SharedStopwatch.StartNew();
            try
            {
                var sessionData = CompletionSessionData.GetOrCreateSessionData(session);

                // As explained in more details in the comments for `CompletionSource.GetCompletionContextAsync`, expanded items might
                // not be provided upon initial trigger of completion to reduce typing delays, even if they are supposed to be included by default.
                // While we do not expect to run in to this scenario very often, we'd still want to minimize the impact on user experience of this feature
                // as best as we could when it does occur. So the solution we came up with is this: if we decided to not include expanded items (because the
                // computation is running too long,) we will let it run in the background as long as the completion session is still active. Then whenever
                // any user input that would cause the completion list to refresh, we will check the state of this background task and add expanded items as part
                // of the update if they are available.
                // There is a `CompletionContext.IsIncomplete` flag, which is only supported in LSP mode at the moment. Therefore we opt to handle the checking
                // and combining the items in Roslyn until the `IsIncomplete` flag is fully supported in classic mode.

                if (sessionData.CombinedSortedList is not null)
                {
                    // Always use the previously saved combined list if available.
                    data = new AsyncCompletionSessionDataSnapshot(sessionData.CombinedSortedList, data.Snapshot, data.Trigger, data.InitialTrigger, data.SelectedFilters,
                        data.IsSoftSelected, data.DisplaySuggestionItem, data.Defaults);
                }
                else if (!ShouldHideExpandedItems() && sessionData.ExpandedItemsTask is not null)
                {
                    var task = sessionData.ExpandedItemsTask;

                    // we don't want to delay showing completion list on waiting for
                    // expanded items, unless responsive typing is disabled by user.
                    if (!sessionData.NonBlockingCompletionEnabled)
                        await task.ConfigureAwait(false);

                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        // Make sure the task is removed when Adding expanded items,
                        // so duplicated items won't be added in subsequent list updates.
                        sessionData.ExpandedItemsTask = null;

                        var (expandedContext, _) = await task.ConfigureAwait(false);
                        if (expandedContext.ItemList.Count > 0)
                        {
                            // Here we rely on the implementation detail of `CompletionItem.CompareTo`, which always put expand items after regular ones.
                            var combinedItemList = session.CreateCompletionList(data.InitialSortedItemList.Concat(expandedContext.ItemList));

                            // Add expanded items into a combined list, and save it to be used for future updates during the same session.
                            sessionData.CombinedSortedList = combinedItemList;
                            var combinedFilterStates = FilterSet.CombineFilterStates(expandedContext.Filters, data.SelectedFilters);

                            data = new AsyncCompletionSessionDataSnapshot(combinedItemList, data.Snapshot, data.Trigger, data.InitialTrigger, combinedFilterStates,
                                data.IsSoftSelected, data.DisplaySuggestionItem, data.Defaults);
                        }

                        AsyncCompletionLogger.LogSessionWithDelayedImportCompletionIncludedInUpdate();
                    }
                }

                var updater = new CompletionListUpdater(session.ApplicableToSpan, sessionData, data, _recentItemsManager, _editorOptionsService.GlobalOptions);
                return await updater.UpdateCompletionListAsync(session, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                AsyncCompletionLogger.LogItemManagerUpdateDataPoint(stopwatch.Elapsed, isCanceled: cancellationToken.IsCancellationRequested);
            }

            // Don't hide expanded items if all these conditions are met:
            // 1. filter text length >= 2 (it's common to use filter of length 2 for camel case match, e.g. `AB` for `ArrayBuiler`)
            // 2. the completion is triggered in the context of listing members (it usually has much fewer items and more often used for browsing purpose)
            // 3. defaults is not empty (it might suggests an expanded item)
            bool ShouldHideExpandedItems()
                => session.ApplicableToSpan.GetText(data.Snapshot).Length < FilterTextLengthToExcludeExpandedItemsExclusive
                    && !IsAfterDot(data.Snapshot, session.ApplicableToSpan)
                    && data.Defaults.IsEmpty;
        }

        private static bool IsAfterDot(ITextSnapshot snapshot, ITrackingSpan applicableToSpan)
        {
            var position = applicableToSpan.GetStartPoint(snapshot).Position;
            return position > 0 && snapshot[position - 1] == '.';
        }

        private sealed class VSItemComparer : IComparer<VSCompletionItem>
        {
            public static VSItemComparer Instance { get; } = new();

            private VSItemComparer()
            {
            }

            public int Compare(VSCompletionItem? x, VSCompletionItem? y)
            {
                if (x is null && y is null)
                    return 0;

                var xRoslyn = x is not null ? CompletionItemData.GetOrAddDummyRoslynItem(x) : null;
                var yRoslyn = y is not null ? CompletionItemData.GetOrAddDummyRoslynItem(y) : null;

                // Sort by default comparer of Roslyn CompletionItem
                return Comparer<RoslynCompletionItem>.Default.Compare(xRoslyn, yRoslyn);
            }
        }
    }
}
