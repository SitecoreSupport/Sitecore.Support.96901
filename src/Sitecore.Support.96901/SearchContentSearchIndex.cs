// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SearchContentSearchIndex.cs" company="Sitecore">
//   Copyright (c) Sitecore. All rights reserved.
// </copyright>
// <summary>
//   SearchContentSearchIndex class.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Sitecore.Support.ContentSearch.Client.Pipelines.Search
{
    using System;
    using Sitecore.ContentSearch;
    using Sitecore.ContentSearch.Client.Pipelines.Search;
    using System.Collections.Generic;
    using System.Linq;
    using Sitecore.ContentSearch.Abstractions;
    using Sitecore.ContentSearch.Diagnostics;
    using Sitecore.ContentSearch.Exceptions;
    using Sitecore.ContentSearch.SearchTypes;
    using Sitecore.ContentSearch.Utilities;
    using Sitecore.Data.Items;
    using Sitecore.Diagnostics;
    using Sitecore.Pipelines.Search;
    using Sitecore.Search;
    using Sitecore.Shell;
    using Sitecore.StringExtensions;

    /// <summary>
    /// Searches the system index.
    /// </summary>
    public class SearchContentSearchIndex
    {
        /// <summary>
        /// The settings.
        /// </summary>
        private ISettings settings;

        /// <summary>
        /// Initializes a new instance of the <see cref="SearchContentSearchIndex"/> class.
        /// </summary>
        public SearchContentSearchIndex()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SearchContentSearchIndex"/> class.
        /// </summary>
        /// <param name="settings">The settings.</param>
        internal SearchContentSearchIndex(ISettings settings)
        {
            this.settings = settings;
        }

        #region Public methods

        /// <summary>
        /// Runs the processor.
        /// </summary>
        /// <param name="args">The arguments.</param>
        public virtual void Process([NotNull] SearchArgs args)
        {
            Assert.ArgumentNotNull(args, "args");

            if (args.UseLegacySearchEngine)
            {
                return;
            }

            if (!ContentSearchManager.Locator.GetInstance<IContentSearchConfigurationSettings>().ContentSearchEnabled())
            {
                args.UseLegacySearchEngine = true;
                return;
            }

            if (!ContentSearchManager.Locator.GetInstance<ISearchIndexSwitchTracker>().IsOn)
            {
                args.IsIndexProviderOn = false;
                return;
            }

            var rootItem = args.Root ?? args.Database.GetRootItem();
            Assert.IsNotNull(rootItem, "rootItem");

            if (args.TextQuery.IsNullOrEmpty())
            {
                return;
            }

            ISearchIndex index;

            try
            {
                index = ContentSearchManager.GetIndex(new SitecoreIndexableItem(rootItem));
            }
            catch (IndexNotFoundException)
            {
                SearchLog.Log.Warn("No index found for " + rootItem.ID);
                return;
            }

            if (!ContentSearchManager.Locator.GetInstance<ISearchIndexSwitchTracker>().IsIndexOn(index.Name))
            {
                args.IsIndexProviderOn = false;
                return;
            }

            if (this.settings == null)
            {
                this.settings = index.Locator.GetInstance<ISettings>();
            }

            using (var context = index.CreateSearchContext())
            {
                List<SitecoreUISearchResultItem> results = new List<SitecoreUISearchResultItem>();

                try
                {
                    IQueryable<SitecoreUISearchResultItem> query = null;

                    if (args.Type != SearchType.ContentEditor)
                    {
                        query = new GenericSearchIndex().Search(args, context);
                    }

                    if (query == null || Enumerable.Count(query) == 0)
                    {
                        if (args.ContentLanguage != null && !args.ContentLanguage.Name.IsNullOrEmpty())
                        {
                            query = context.GetQueryable<SitecoreUISearchResultItem>().Where(i => i.Name.StartsWith(args.TextQuery) || (i.Content.Contains(args.TextQuery) && i.Language.Equals(args.ContentLanguage.Name)));
                        }
                        else
                        {
                            query = context.GetQueryable<SitecoreUISearchResultItem>().Where(i => i.Name.StartsWith(args.TextQuery) || i.Content.Contains(args.TextQuery));
                        }
                    }

                    // In content editor, we search the entire tree even if the root is supplied. If it is, the results will get special categorization treatment later on in the pipeline.
                    if (args.Root != null && args.Type != SearchType.ContentEditor)
                    {
                        query = query.Where(i => i.Paths.Contains(args.Root.ID));
                    }

                    foreach (var result in Enumerable.TakeWhile(query, result => results.Count < args.Limit))
                    {
                        if (!UserOptions.View.ShowHiddenItems)
                        {
                            var item = this.GetSitecoreItem(result);
                            if (item != null && this.IsHidden(item))
                            {
                                continue;
                            }
                        }

                        var resultForSameItem = results.FirstOrDefault(r => r.ItemId == result.ItemId);
                        if (resultForSameItem == null)
                        {
                            results.Add(result);
                            continue;
                        }

                        if (args.ContentLanguage != null && !args.ContentLanguage.Name.IsNullOrEmpty())
                        {
                            if ((resultForSameItem.Language != args.ContentLanguage.Name && result.Language == args.ContentLanguage.Name)
                              || (resultForSameItem.Language == result.Language && resultForSameItem.Uri.Version.Number < result.Uri.Version.Number))
                            {
                                results.Remove(resultForSameItem);
                                results.Add(result);
                            }
                        }
                        else if (args.Type != SearchType.Classic)
                        {
                            if (resultForSameItem.Language == result.Language && resultForSameItem.Uri.Version.Number < result.Uri.Version.Number)
                            {
                                results.Remove(resultForSameItem);
                                results.Add(result);
                            }
                        }
                        else
                        {
                            results.Add(result);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Log.Error("Invalid lucene search query: " + args.TextQuery, e, this);
                    return;
                }

                this.FillSearchResult(results, args);
            }
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Fills the search result.
        /// </summary>
        /// <param name="searchResult">The search result.</param>
        /// <param name="args">The arguments.</param>
        protected virtual void FillSearchResult(IList<SitecoreUISearchResultItem> searchResult, SearchArgs args)
        {
            foreach (var result in searchResult)
            {
                var sitecoreItem = this.GetSitecoreItem(result);
                if (sitecoreItem == null)
                {
                    // item either does not exist or security protected.
                    // According to the requirements from management, defined in the TFS:96901
                    // processor should not return non-Sitecore items.
                    continue;
                }

                var title = result.DisplayName ?? result.Name;
                if (title == null)
                {
                    continue;
                }

                object icon = result.Fields.Find(pair => pair.Key == BuiltinFields.Icon).Value
                            ?? sitecoreItem.Appearance.Icon ?? this.settings?.DefaultIcon();

                if (icon == null)
                {
                    continue;
                }

                string url = string.Empty;
                if (result.Uri != null)
                {
                    url = result.Uri.ToString();
                }

                args.Result.AddResult(new SearchResult(title, icon.ToString(), url));
            }
        }

        /// <summary>
        /// Gets the sitecore item.
        /// </summary>
        /// <param name="searchItem">The search item that corresponds to the search result.</param>
        /// <returns>The Sitecore item.</returns>
        protected virtual Item GetSitecoreItem(SitecoreUISearchResultItem searchItem)
        {
            if (searchItem == null)
            {
                return null;
            }

            try
            {
                return searchItem.GetItem();
            }
            catch (NullReferenceException)
            {
            }

            return null;
        }

        /// <summary>
        /// Determines whether the specified item is hidden.
        /// </summary>
        /// <param name="item">
        /// The item.
        /// </param>
        /// <returns>
        /// <c>true</c> if the specified item is hidden; otherwise, <c>false</c>.
        /// </returns>
        private bool IsHidden([NotNull] Item item)
        {
            Assert.ArgumentNotNull(item, "item");

            return item.Appearance.Hidden || (item.Parent != null && this.IsHidden(item.Parent));
        }

        #endregion
    }
}