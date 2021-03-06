﻿using System;
using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Models;
using Foundatio.Caching;
using Nest;
using NLog.Fluent;
using System.Linq.Expressions;
using Exceptionless.Core.Repositories.Configuration;
using Exceptionless.Core.Utility;

namespace Exceptionless.Core.Repositories {
    public class FindResults<T> {
        public FindResults() {
            Documents = new List<T>();
        } 

        public ICollection<T> Documents { get; set; }
        public long Total { get; set; }
    }

    public abstract class ElasticSearchReadOnlyRepository<T> : IReadOnlyRepository<T> where T : class, IIdentity, new() {
        protected readonly static bool _supportsSoftDeletes = typeof(ISupportSoftDeletes).IsAssignableFrom(typeof(T));
        private static readonly DateTime MIN_OBJECTID_DATE = new DateTime(2000, 1, 1);
        protected static readonly string _entityType = typeof(T).Name;
        protected static readonly bool _isEvent = typeof(T) == typeof(PersistentEvent);
        protected static readonly bool _isStack = typeof(T) == typeof(Stack);

        protected readonly IElasticClient _elasticClient;
        protected readonly IElasticSearchIndex _index;

        protected ElasticSearchReadOnlyRepository(IElasticClient elasticClient, IElasticSearchIndex index, ICacheClient cacheClient = null) {
            _elasticClient = elasticClient;
            _index = index;
            Cache = cacheClient;
            EnableCache = cacheClient != null;
        }

        public bool EnableCache { get; protected set; }

        protected ICacheClient Cache { get; private set; }

        protected virtual string[] GetIndices() {
            return _index != null ? new[] { _index.Name } : new string[0];
        }

        protected virtual string GetTypeName() {
            return _entityType.ToLower();
        }

        protected void InvalidateCache(string cacheKey, bool autoScopeCacheKey = true)
        {
            if (!EnableCache || Cache == null)
                return;

            Cache.Remove(autoScopeCacheKey ? GetScopedCacheKey(cacheKey) : cacheKey);
        }

        protected virtual void InvalidateCache(ICollection<T> documents, ICollection<T> originalDocuments)
        {
            if (!EnableCache || Cache == null)
                return;

            if (documents == null)
                throw new ArgumentNullException("documents");

            documents.ForEach(d => Cache.Remove(GetScopedCacheKey(d.Id)));
        }

        public void InvalidateCache(T document) {
            InvalidateCache(new[] { document });
        }

        public void InvalidateCache(ICollection<T> documents) {
            InvalidateCache(documents, null);
        }

        protected string GetScopedCacheKey(string cacheKey) {
            return String.Concat(GetTypeName(), "-", cacheKey);
        }

        protected FindResults<T> Find(ElasticSearchOptions<T> options) {
            return FindAs(options);
        }

        protected FindResults<T> FindAs(ElasticSearchOptions<T> options) {
            if (options == null)
                throw new ArgumentNullException("options");

            FindResults<T> result = null;
            if (EnableCache) {
                if (options.UseCache) {
                    result = Cache.Get<FindResults<T>>(GetScopedCacheKey(options.CacheKey));
                    Log.Trace().Message("Cache {0}: type={1}", result != null ? "hit" : "miss", _entityType).Write();
                }

                if (result != null)
                    return result;
            }
            
            var searchDescriptor = options.SortBy.Count == 0 ?
                new SearchDescriptor<T>().Query(options.GetElasticSearchQuery(_supportsSoftDeletes))
                : new SearchDescriptor<T>().Filter(options.GetElasticSearchFilter(_supportsSoftDeletes));

            searchDescriptor.Indices(options.Indices.Any() ? options.Indices.ToArray() : GetIndices());
            searchDescriptor.IgnoreUnavailable();
            searchDescriptor.Size(options.GetLimit());
            searchDescriptor.Type(typeof(T));

            if (options.UsePaging)
                searchDescriptor.Skip(options.GetSkip());

            if (options.Fields.Count > 0)
                searchDescriptor.Source(s => s.Include(options.Fields.ToArray()));
            else
                searchDescriptor.Source(s => s.Exclude("idx"));

            if (options.SortBy.Count > 0)
                foreach (var sort in options.SortBy)
                    searchDescriptor.Sort(sort);

            _elasticClient.EnableTrace();
            var results = _elasticClient.Search<T>(searchDescriptor);
            _elasticClient.DisableTrace();

            if (!results.IsValid)
                throw new ApplicationException(String.Format("ElasticSearch error code \"{0}\".", results.ConnectionStatus.HttpStatusCode), results.ConnectionStatus.OriginalException);

            options.HasMore = options.UseLimit && results.Total > options.GetLimit();

            result = new FindResults<T> {
                Documents = results.Documents.ToList(),
                Total = results.Total
            };

            if (EnableCache && options.UseCache)
                Cache.Set(GetScopedCacheKey(options.CacheKey), result, options.GetCacheExpirationDate());

            return result;
        }


        protected T FindOne(OneOptions options) {
            return FindOneAs(options);
        }

        protected T FindOneAs(OneOptions options) {
            if (options == null)
                throw new ArgumentNullException("options");

            T result = null;

            if (EnableCache) {
                if (options.UseCache) {
                    result = Cache.Get<T>(GetScopedCacheKey(options.CacheKey));
                    Log.Trace().Message("Cache {0}: type={1}", result != null ? "hit" : "miss", _entityType).Write();
                }

                if (result != null)
                    return result;
            }

            var searchDescriptor = new SearchDescriptor<T>().Filter(options.GetElasticSearchFilter<T>(_supportsSoftDeletes)).Size(1);
            if (options.Fields.Count > 0)
                searchDescriptor.Source(s => s.Include(options.Fields.ToArray()));
            else
                searchDescriptor.Source(s => s.Exclude("idx"));

            var elasticSearchOptions = options as ElasticSearchOptions<T>;
            searchDescriptor.Indices(elasticSearchOptions != null && elasticSearchOptions.Indices.Any() ? elasticSearchOptions.Indices.ToArray() : GetIndices());

            if (elasticSearchOptions != null && elasticSearchOptions.SortBy.Count > 0) {
                foreach (var sort in elasticSearchOptions.SortBy)
                    searchDescriptor.Sort(sort);
            }

            _elasticClient.EnableTrace();
            result = _elasticClient.Search<T>(searchDescriptor).Documents.FirstOrDefault();
            _elasticClient.DisableTrace();
            
            if (EnableCache && result != null && options.UseCache)
                Cache.Set(GetScopedCacheKey(options.CacheKey), result, options.GetCacheExpirationDate());

            return result;
        }
        
        public bool Exists(string id) {
            if (String.IsNullOrEmpty(id))
                return false;

            return Exists(new OneOptions().WithId(id));
        }

        protected bool Exists(OneOptions options) {
            if (options == null)
                throw new ArgumentNullException("options");

            options.Fields.Add("id");
            var searchDescriptor = new SearchDescriptor<T>().Filter(options.GetElasticSearchFilter<T>(_supportsSoftDeletes)).Size(1);

            var elasticSearchOptions = options as ElasticSearchOptions<T>;
            searchDescriptor.Indices(elasticSearchOptions != null && elasticSearchOptions.Indices.Any()
                ? elasticSearchOptions.Indices.ToArray()
                : GetIndices()); 
            if (elasticSearchOptions != null && elasticSearchOptions.SortBy.Count > 0)
            {
                foreach (var sort in elasticSearchOptions.SortBy)
                    searchDescriptor.Sort(sort);
            }

            return _elasticClient.Search<T>(searchDescriptor).HitsMetaData.Total > 0;
        }

        protected long Count(ElasticSearchOptions<T> options) {
            if (options == null)
                throw new ArgumentNullException("options");

            long? result;
            if (EnableCache && options.UseCache) {
                result = Cache.Get<long?>(GetScopedCacheKey("count-" + options.CacheKey));
                if (result.HasValue)
                    return result.Value;
            }

            var countDescriptor = new CountDescriptor<T>().Query(f => f.Filtered(s => s.Filter(f2 => options.GetElasticSearchFilter(_supportsSoftDeletes))));
            countDescriptor.Indices(options.Indices.Any()
                ? options.Indices.ToArray()
                : GetIndices());
			
            countDescriptor.IgnoreUnavailable();

            countDescriptor.Type(typeof(T));

            _elasticClient.EnableTrace();
            var results = _elasticClient.Count<T>(countDescriptor);
            _elasticClient.DisableTrace();

            if (!results.IsValid)
                throw new ApplicationException(String.Format("ElasticSearch error code \"{0}\".", results.ConnectionStatus.HttpStatusCode), results.ConnectionStatus.OriginalException);

            result = results.Count;

            if (EnableCache && options.UseCache)
                Cache.Set(GetScopedCacheKey("count-" + options.CacheKey), result, options.GetCacheExpirationDate());

            return result.Value;
        }

        protected IDictionary<string, long> SimpleAggregation(ElasticSearchOptions<T> options, Expression<Func<T, object>> fieldExpression)
        {
            var searchDescriptor = new SearchDescriptor<T>()
                .Query(f => f.Filtered(s => s.Filter(f2 => options.GetElasticSearchFilter(_supportsSoftDeletes))))
                .Aggregations(a => a.Terms("simple", sel => sel.Field(fieldExpression).Size(10)));
            
            searchDescriptor.Indices(options.Indices.Any()
                ? options.Indices.ToArray()
                : GetIndices());

            _elasticClient.EnableTrace();
            var aggResults = _elasticClient.Search<T>(searchDescriptor);
            _elasticClient.DisableTrace();

            var results = new Dictionary<string, long>();

            foreach (var ar in aggResults.Aggs.Terms("simple").Items) {
                results.Add(ar.Key, ar.DocCount);
            }

            return results;
        }

        public long Count() {
            return _elasticClient.Count<T>(c => c.Query(q => q.MatchAll()).Indices(GetIndices())).Count;
        }

        public T GetById(string id, bool useCache = false, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(id))
                return null;

            T result = null;
            if (EnableCache && useCache) {
                result = Cache.Get<T>(GetScopedCacheKey(id));
                if (result != null)
                    return result;
            }

            // try using the object id to figure out what index the entity is located in
            string index = GetIndexName(id);
            if (index != null) {
                _elasticClient.EnableTrace();
                result = _elasticClient.Get<T>(f => f.Id(id).Index(index).SourceExclude("idx")).Source;
                _elasticClient.DisableTrace();
            }

            // TODO:see if we can get rid of this.
            // fallback to doing a find
            if (result == null)
                result = FindOne(new OneOptions().WithId(id).WithCacheKey(EnableCache && useCache ? id : null).WithExpiresIn(expiresIn));

            if (EnableCache && result != null && useCache)
                Cache.Set(GetScopedCacheKey(id), result, expiresIn ?? TimeSpan.FromSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS));

            return result;
        }

        public FindResults<T> GetByIds(ICollection<string> ids, PagingOptions paging = null, bool useCache = false, TimeSpan? expiresIn = null) {
            if (ids == null || ids.Count == 0)
                return new FindResults<T>();

            var results = new List<T>();
            if (EnableCache && useCache) {
                results.AddRange(ids.Select(id => Cache.Get<T>(GetScopedCacheKey(id))).Where(cacheHit => cacheHit != null));

                var notCachedIds = ids.Except(results.Select(i => i.Id)).ToArray();
                if (notCachedIds.Length == 0)
                    return new FindResults<T> { Documents = results, Total = results.Count };
            }

            // try using the object id to figure out what index the entity is located in
            var foundItems = new List<T>();
            var itemsToFind = new List<string>();
            var multiGet = new MultiGetDescriptor();
			
            // TODO Use the index..
            foreach (var id in ids.Except(results.Select(i => i.Id))) {
                string index = GetIndexName(id);
                if (index != null)
                    multiGet.Get<T>(f => f.Id(id).Index(index).Source(s => s.Exclude("idx")));
                else
                    itemsToFind.Add(id);
            }

            _elasticClient.EnableTrace();
            foreach (var doc in _elasticClient.MultiGet(multiGet).Documents) {
                if (doc.Found)
                    foundItems.Add(doc.Source as T);
                else
                    itemsToFind.Add(doc.Id);
            }
            _elasticClient.DisableTrace();

            // fallback to doing a find
            if (itemsToFind.Count > 0)
                foundItems.AddRange(Find(new ElasticSearchOptions<T>().WithIds(itemsToFind)).Documents);

            if (EnableCache && useCache && foundItems.Count > 0) {
                foreach (var item in foundItems)
                    Cache.Set(GetScopedCacheKey(item.Id), item, expiresIn.HasValue ? DateTime.Now.Add(expiresIn.Value) : DateTime.Now.AddSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS));
            }

            results.AddRange(foundItems);
            return new FindResults<T> {
                Documents = results,
                Total = results.Count
            };
        }

        private string GetIndexName(string id) {
            if (_isEvent) {
                ObjectId objectId;
                if (ObjectId.TryParse(id, out objectId) && objectId.CreationTime > MIN_OBJECTID_DATE)
                    return String.Concat(_index.VersionedName, "-", objectId.CreationTime.ToString("yyyyMM"));

                return null;
            }

            return _index.VersionedName;
        }

        public FindResults<T> GetAll(string sort = null, SortOrder sortOrder = SortOrder.Ascending, PagingOptions paging = null)
        {
            var search = new ElasticSearchOptions<T>()
                .WithPaging(paging)
                .WithSort(sort, sortOrder);

            return Find(search);
        }

        public FindResults<T> GetBySearch(string systemFilter, string userFilter = null, string query = null, string sort = null, SortOrder sortOrder = SortOrder.Ascending, PagingOptions paging = null)
        {
            var search = new ElasticSearchOptions<T>()
                .WithSystemFilter(systemFilter)
                .WithFilter(userFilter)
                .WithQuery(query, false)
                .WithSort(sort, sortOrder)
                .WithPaging(paging);

            return Find(search);
        }
    }
}